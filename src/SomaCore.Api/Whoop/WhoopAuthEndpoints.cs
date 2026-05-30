using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Api.Whoop;

public static class WhoopAuthEndpoints
{
    private const string StateCookieName = "somacore.whoop.state";

    public static IEndpointRouteBuilder MapWhoopAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth/whoop").RequireAuthorization();

        group.MapGet("/start",     StartAsync);
        group.MapGet("/callback",  CallbackAsync);
        group.MapPost("/disconnect", DisconnectAsync);

        return app;
    }

    private static async Task<IResult> StartAsync(
        HttpContext httpContext,
        SomaCoreDbContext db,
        IWhoopOAuthClient whoop,
        IWhoopStateProtector stateProtector,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Whoop.Auth.Start");

        if (!Guid.TryParse(httpContext.User.GetObjectId(), out var entraOid))
        {
            return Results.Problem("No Entra OID claim on the signed-in user.", statusCode: 400);
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);

        if (user is null)
        {
            // The JIT middleware should have created this row; if not, fail loudly.
            return Results.Problem("SomaCore user row not found for the signed-in identity.", statusCode: 500);
        }

        var state = new WhoopOAuthState(user.Id, WhoopStateProtector.NewNonce(), DateTimeOffset.UtcNow);
        var protectedState = stateProtector.Protect(state);

        httpContext.Response.Cookies.Append(StateCookieName, protectedState, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // OAuth callback is a top-level GET cross-site redirect
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/auth/whoop",
        });

        db.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = user.Id,
            Source = OAuthAuditSource.Whoop,
            Action = OAuthAuditAction.Authorize,
            Success = true,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new { state.Nonce })),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        var authorizeUrl = whoop.BuildAuthorizeUrl(protectedState);
        logger.LogInformation(
            "Redirecting SomaCore user {SomaCoreUserId} to WHOOP authorize",
            user.Id);
        return Results.Redirect(authorizeUrl);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext httpContext,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        SomaCoreDbContext db,
        IWhoopOAuthClient whoop,
        IWhoopStateProtector stateProtector,
        IKeyVaultSecretsClient kv,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Whoop.Auth.Callback");

        // Always pop the cookie; we never want to reuse it.
        httpContext.Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/auth/whoop" });

        if (!string.IsNullOrEmpty(error))
        {
            await LogAuditAsync(db, null, success: false,
                error: $"{error}: {errorDescription}", cancellationToken);
            logger.LogWarning("WHOOP returned error {Error}: {Description}", error, errorDescription);
            return Results.Redirect("/me?whoop=cancelled");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            await LogAuditAsync(db, null, success: false,
                error: "missing code or state on callback", cancellationToken);
            return Results.BadRequest("Missing code or state.");
        }

        if (!httpContext.Request.Cookies.TryGetValue(StateCookieName, out var cookieValue) ||
            !string.Equals(cookieValue, state, StringComparison.Ordinal))
        {
            await LogAuditAsync(db, null, success: false,
                error: "state cookie missing or did not match query state", cancellationToken);
            return Results.BadRequest("Invalid state.");
        }

        var unwrapped = stateProtector.Unprotect(state);
        if (unwrapped is null)
        {
            await LogAuditAsync(db, null, success: false,
                error: "state failed to decrypt or expired", cancellationToken);
            return Results.BadRequest("Invalid state.");
        }

        var somacoreUserId = unwrapped.SomaCoreUserId;

        // Cross-check the cookie's user against the currently signed-in user.
        if (Guid.TryParse(httpContext.User.GetObjectId(), out var entraOid))
        {
            var matches = await db.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == somacoreUserId && u.EntraOid == entraOid, cancellationToken);
            if (!matches)
            {
                await LogAuditAsync(db, somacoreUserId, success: false,
                    error: "callback user did not match cookie user", cancellationToken);
                return Results.BadRequest("Authentication mismatch on callback.");
            }
        }

        var tokenResult = await whoop.ExchangeCodeAsync(code, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            await LogAuditAsync(db, somacoreUserId, success: false,
                error: tokenResult.Error!, cancellationToken);
            return Results.Redirect("/me?whoop=failed");
        }

        var token = tokenResult.Value!;
        var profileResult = await whoop.GetBasicProfileAsync(token.AccessToken, cancellationToken);
        if (!profileResult.IsSuccess)
        {
            await LogAuditAsync(db, somacoreUserId, success: false,
                error: $"profile fetch failed: {profileResult.Error}", cancellationToken);
            return Results.Redirect("/me?whoop=failed");
        }

        var profile = profileResult.Value!;
        var secretName = $"whoop-refresh-{somacoreUserId}";

        // Mark any existing active row for (user, whoop) revoked, then insert a fresh active.
        var existing = await db.ExternalConnections
            .Where(c => c.UserId == somacoreUserId
                     && c.Source == ConnectionSource.Whoop
                     && c.Status == ConnectionStatus.Active)
            .ToListAsync(cancellationToken);
        foreach (var c in existing)
        {
            c.Status = ConnectionStatus.Revoked;
        }

        var metadata = new JsonObject
        {
            ["whoop_user_id"] = profile.UserId,
            ["whoop_email"]   = profile.Email,
        };
        if (!string.IsNullOrEmpty(profile.FirstName)) metadata["whoop_first_name"] = profile.FirstName;
        if (!string.IsNullOrEmpty(profile.LastName))  metadata["whoop_last_name"]  = profile.LastName;

        var connection = new ExternalConnection
        {
            UserId = somacoreUserId,
            Source = ConnectionSource.Whoop,
            Status = ConnectionStatus.Active,
            Scopes = (token.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            KeyVaultSecretName = secretName,
            LastRefreshAt = DateTimeOffset.UtcNow,
            NextRefreshAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds * 0.85),
            ConnectionMetadata = JsonDocument.Parse(metadata.ToJsonString()),
        };
        db.ExternalConnections.Add(connection);

        await kv.SetSecretAsync(secretName, token.RefreshToken, cancellationToken);

        db.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = somacoreUserId,
            ExternalConnectionId = connection.Id,
            Source = OAuthAuditSource.Whoop,
            Action = OAuthAuditAction.CallbackSuccess,
            Success = true,
            HttpStatusCode = 200,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                whoop_user_id = profile.UserId,
                scope = token.Scope,
            })),
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "WHOOP connection succeeded for SomaCore user {SomaCoreUserId} (whoop_user_id={WhoopUserId})",
            somacoreUserId,
            profile.UserId);

        return Results.Redirect("/me?whoop=connected");
    }

    internal static async Task<IResult> DisconnectAsync(
        HttpContext httpContext,
        SomaCoreDbContext db,
        IWhoopOAuthClient whoop,
        IWhoopAccessTokenCache tokenCache,
        IKeyVaultSecretsClient kv,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Whoop.Auth.Disconnect");

        if (!Guid.TryParse(httpContext.User.GetObjectId(), out var entraOid))
        {
            return Results.Problem("No Entra OID claim on the signed-in user.", statusCode: 400);
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);
        if (user is null)
        {
            return Results.Problem("SomaCore user row not found for the signed-in identity.", statusCode: 500);
        }

        // Disconnect targets any non-revoked WHOOP row for this user. Revoked
        // rows are already torn down — no point in 404'ing on those either, but
        // we want the freshest live row if both exist (callback creates a new
        // row each time and marks prior ones revoked).
        var connection = await db.ExternalConnections
            .Where(c => c.UserId == user.Id
                     && c.Source == ConnectionSource.Whoop
                     && c.Status != ConnectionStatus.Revoked)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            return Results.NotFound("No active WHOOP connection to disconnect.");
        }

        var connectionId = connection.Id;
        var secretName = connection.KeyVaultSecretName;

        // Step 1: try to get a fresh access token so we can call WHOOP revoke.
        // If the connection is already in refresh_failed, the cache will fail
        // here — that's fine, we log it and proceed straight to local teardown.
        var revokeOutcome = "skipped"; // logged into the audit row
        var tokenResult = await tokenCache.GetAccessTokenAsync(connectionId, cancellationToken);
        if (tokenResult.IsSuccess)
        {
            // Step 2: best-effort revoke at WHOOP. Whether this succeeds or
            // fails, we proceed to local teardown — the user's intent is to
            // disconnect, and a stale grant at WHOOP without a local row is
            // harmless (we can no longer use it).
            try
            {
                var revoke = await whoop.RevokeAccessAsync(tokenResult.Value!, cancellationToken);
                revokeOutcome = revoke.IsSuccess ? "success" : $"failed: {revoke.Error}";
                if (!revoke.IsSuccess)
                {
                    logger.LogWarning(
                        "WHOOP revoke returned failure for connection {ConnectionId}: {Error} — proceeding with local teardown",
                        connectionId,
                        revoke.Error);
                }
            }
            catch (Exception ex)
            {
                // 5xx / transport / timeout etc. — same disposition as a 4xx:
                // log and proceed with local teardown.
                revokeOutcome = $"exception: {ex.GetType().Name}";
                logger.LogWarning(ex,
                    "WHOOP revoke threw for connection {ConnectionId} — proceeding with local teardown",
                    connectionId);
            }
        }
        else
        {
            revokeOutcome = $"no_access_token: {tokenResult.Error}";
            logger.LogInformation(
                "Skipping WHOOP revoke for connection {ConnectionId} — could not obtain access token ({Error})",
                connectionId,
                tokenResult.Error);
        }

        // Step 3: hard-delete the external_connections row. whoop_recoveries
        // rows for this user have external_connection_id ON DELETE SET NULL,
        // so the recovery history is preserved (anonymized to the connection).
        // oauth_audit rows also SET NULL on the FK so the audit trail survives.
        db.ExternalConnections.Remove(connection);

        // Step 4: audit. Insert before saving so it sees the connection row
        // still in the change tracker (ExternalConnectionId column on the
        // audit row gets NULL'd via SET NULL when the connection delete is
        // applied — that's intentional, we still log who/when/what).
        db.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = user.Id,
            ExternalConnectionId = null, // explicit: the FK will be SET NULL on connection delete anyway
            Source = OAuthAuditSource.Whoop,
            Action = OAuthAuditAction.ManualDisconnect,
            Success = true,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                revoke = revokeOutcome,
                deleted_connection_id = connectionId,
            })),
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken);

        // Step 5: best-effort delete of the refresh token in Key Vault. If
        // this fails the token is still useless (the connection is gone, no
        // code path reads this secret) — we just leave it for a human cleanup.
        var kvDeleted = await kv.TryDeleteSecretAsync(secretName, cancellationToken);
        if (!kvDeleted)
        {
            logger.LogWarning(
                "Failed to delete Key Vault secret {SecretName} after disconnecting connection {ConnectionId}",
                secretName,
                connectionId);
        }

        logger.LogInformation(
            "Disconnected WHOOP for SomaCore user {SomaCoreUserId} (connection {ConnectionId}, revoke={RevokeOutcome}, kv_deleted={KvDeleted})",
            user.Id,
            connectionId,
            revokeOutcome,
            kvDeleted);

        return Results.Redirect("/me?whoop=disconnected");
    }

    private static Task LogAuditAsync(
        SomaCoreDbContext db,
        Guid? somacoreUserId,
        bool success,
        string error,
        CancellationToken ct)
    {
        db.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = somacoreUserId,
            Source = OAuthAuditSource.Whoop,
            Action = success ? OAuthAuditAction.CallbackSuccess : OAuthAuditAction.CallbackFailed,
            Success = success,
            ErrorMessage = error.Length > 1000 ? error[..1000] : error,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        return db.SaveChangesAsync(ct);
    }
}
