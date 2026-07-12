using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Strava;

namespace SomaCore.Api.Strava;

/// <summary>
/// Strava OAuth connect/disconnect — mirrors <see cref="Whoop.WhoopAuthEndpoints"/>.
/// Every handler is gated on <see cref="StravaOptions.Enabled"/> (404 when off):
/// the flag defaults false and stays false in dev until Adam flips it at deploy
/// time, so none of this is user-reachable until then.
/// </summary>
public static class StravaAuthEndpoints
{
    private const string StateCookieName = "somacore.strava.state";

    public static IEndpointRouteBuilder MapStravaAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth/strava").RequireAuthorization();

        group.MapGet("/start", StartAsync);
        group.MapGet("/callback", CallbackAsync);
        group.MapPost("/disconnect", DisconnectAsync);

        return app;
    }

    internal static async Task<IResult> StartAsync(
        HttpContext httpContext,
        SomaCoreDbContext db,
        IStravaOAuthClient strava,
        IStravaStateProtector stateProtector,
        IOptions<StravaOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        var logger = loggerFactory.CreateLogger("Strava.Auth.Start");

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

        var state = new StravaOAuthState(user.Id, StravaStateProtector.NewNonce(), DateTimeOffset.UtcNow);
        var protectedState = stateProtector.Protect(state);

        httpContext.Response.Cookies.Append(StateCookieName, protectedState, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // OAuth callback is a top-level GET cross-site redirect
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/auth/strava",
        });

        db.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = user.Id,
            Source = OAuthAuditSource.Strava,
            Action = OAuthAuditAction.Authorize,
            Success = true,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new { state.Nonce })),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        var authorizeUrl = strava.BuildAuthorizeUrl(protectedState);
        logger.LogInformation(
            "Redirecting SomaCore user {SomaCoreUserId} to Strava authorize",
            user.Id);
        return Results.Redirect(authorizeUrl);
    }

    internal static async Task<IResult> CallbackAsync(
        HttpContext httpContext,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? scope,
        [FromQuery] string? error,
        SomaCoreDbContext db,
        IStravaOAuthClient strava,
        IStravaStateProtector stateProtector,
        IKeyVaultSecretsClient kv,
        IOptions<StravaOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        var logger = loggerFactory.CreateLogger("Strava.Auth.Callback");

        // Always pop the cookie; we never want to reuse it.
        httpContext.Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/auth/strava" });

        if (!string.IsNullOrEmpty(error))
        {
            // Strava sends error=access_denied when the user cancels the grant.
            await LogAuditAsync(db, null, success: false, error: error, cancellationToken);
            logger.LogWarning("Strava returned error {Error}", error);
            return Results.Redirect("/me?strava=cancelled");
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

        var tokenResult = await strava.ExchangeCodeAsync(code, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            await LogAuditAsync(db, somacoreUserId, success: false,
                error: tokenResult.Error!, cancellationToken);
            return Results.Redirect("/me?strava=failed");
        }

        var token = tokenResult.Value!;

        // Strava inlines the athlete summary on the code exchange — no separate
        // profile fetch (unlike WHOOP). Without it we can't key the connection.
        if (token.Athlete is null)
        {
            await LogAuditAsync(db, somacoreUserId, success: false,
                error: "token exchange response carried no athlete object", cancellationToken);
            return Results.Redirect("/me?strava=failed");
        }

        // Fresh secret name per connect — a soft-deleted KV secret name cannot
        // be reused for SetSecret (409). Same rationale as the WHOOP callback.
        var secretName = $"strava-refresh-{somacoreUserId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        // Mark any existing active row for (user, strava) revoked, then insert a fresh active.
        var existing = await db.ExternalConnections
            .Where(c => c.UserId == somacoreUserId
                     && c.Source == ConnectionSource.Strava
                     && c.Status == ConnectionStatus.Active)
            .ToListAsync(cancellationToken);
        foreach (var c in existing)
        {
            c.Status = ConnectionStatus.Revoked;
        }

        var metadata = new JsonObject
        {
            ["strava_athlete_id"] = token.Athlete.Id,
        };
        if (!string.IsNullOrEmpty(token.Athlete.Username)) metadata["strava_username"] = token.Athlete.Username;
        if (!string.IsNullOrEmpty(token.Athlete.FirstName)) metadata["strava_first_name"] = token.Athlete.FirstName;
        if (!string.IsNullOrEmpty(token.Athlete.LastName)) metadata["strava_last_name"] = token.Athlete.LastName;

        // Granted scopes arrive as a comma-separated callback query param —
        // Strava's token body has no scope field. The user can down-scope on
        // the grant screen; store what was actually granted.
        var grantedScopes = (scope ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var connection = new ExternalConnection
        {
            UserId = somacoreUserId,
            Source = ConnectionSource.Strava,
            Status = ConnectionStatus.Active,
            Scopes = grantedScopes,
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
            Source = OAuthAuditSource.Strava,
            Action = OAuthAuditAction.CallbackSuccess,
            Success = true,
            HttpStatusCode = 200,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                strava_athlete_id = token.Athlete.Id,
                scope,
            })),
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Strava connection succeeded for SomaCore user {SomaCoreUserId} (strava_athlete_id={StravaAthleteId})",
            somacoreUserId,
            token.Athlete.Id);

        return Results.Redirect("/me?strava=connected");
    }

    internal static async Task<IResult> DisconnectAsync(
        HttpContext httpContext,
        SomaCoreDbContext db,
        IStravaOAuthClient strava,
        IStravaAccessTokenCache tokenCache,
        IKeyVaultSecretsClient kv,
        IOptions<StravaOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        var logger = loggerFactory.CreateLogger("Strava.Auth.Disconnect");

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

        var connection = await db.ExternalConnections
            .Where(c => c.UserId == user.Id
                     && c.Source == ConnectionSource.Strava
                     && c.Status != ConnectionStatus.Revoked)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            return Results.NotFound("No active Strava connection to disconnect.");
        }

        var connectionId = connection.Id;
        var secretName = connection.KeyVaultSecretName;

        // Best-effort deauthorize at Strava, then local teardown regardless —
        // same shape and rationale as WhoopAuthEndpoints.DisconnectAsync.
        var revokeOutcome = "skipped"; // logged into the audit row
        var tokenResult = await tokenCache.GetAccessTokenAsync(connectionId, cancellationToken);
        if (tokenResult.IsSuccess)
        {
            try
            {
                var revoke = await strava.DeauthorizeAsync(tokenResult.Value!, cancellationToken);
                revokeOutcome = revoke.IsSuccess ? "success" : $"failed: {revoke.Error}";
                if (!revoke.IsSuccess)
                {
                    logger.LogWarning(
                        "Strava deauthorize returned failure for connection {ConnectionId}: {Error} — proceeding with local teardown",
                        connectionId,
                        revoke.Error);
                }
            }
            catch (Exception ex)
            {
                revokeOutcome = $"exception: {ex.GetType().Name}";
                logger.LogWarning(ex,
                    "Strava deauthorize threw for connection {ConnectionId} — proceeding with local teardown",
                    connectionId);
            }
        }
        else
        {
            revokeOutcome = $"no_access_token: {tokenResult.Error}";
            logger.LogInformation(
                "Skipping Strava deauthorize for connection {ConnectionId} — could not obtain access token ({Error})",
                connectionId,
                tokenResult.Error);
        }

        // Hard-delete the connection row. strava_activities rows have
        // external_connection_id ON DELETE SET NULL, so activity history is
        // preserved; oauth_audit rows also SET NULL so the trail survives.
        db.ExternalConnections.Remove(connection);

        db.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = user.Id,
            ExternalConnectionId = null, // explicit: the FK will be SET NULL on connection delete anyway
            Source = OAuthAuditSource.Strava,
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

        // Best-effort delete of the refresh token in Key Vault.
        var kvDeleted = await kv.TryDeleteSecretAsync(secretName, cancellationToken);
        if (!kvDeleted)
        {
            logger.LogWarning(
                "Failed to delete Key Vault secret {SecretName} after disconnecting connection {ConnectionId}",
                secretName,
                connectionId);
        }

        logger.LogInformation(
            "Disconnected Strava for SomaCore user {SomaCoreUserId} (connection {ConnectionId}, revoke={RevokeOutcome}, kv_deleted={KvDeleted})",
            user.Id,
            connectionId,
            revokeOutcome,
            kvDeleted);

        return Results.Redirect("/me?strava=disconnected");
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
            Source = OAuthAuditSource.Strava,
            Action = success ? OAuthAuditAction.CallbackSuccess : OAuthAuditAction.CallbackFailed,
            Success = success,
            ErrorMessage = error.Length > 1000 ? error[..1000] : error,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        return db.SaveChangesAsync(ct);
    }
}
