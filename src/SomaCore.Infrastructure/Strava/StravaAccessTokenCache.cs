using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;

namespace SomaCore.Infrastructure.Strava;

public interface IStravaAccessTokenCache
{
    /// <summary>
    /// Resolve a fresh access token for the given external_connection. On cache miss,
    /// reads the refresh token from KV, rotates via Strava, persists the new refresh
    /// token to KV, and updates connection metadata.
    /// </summary>
    Task<Result<string>> GetAccessTokenAsync(Guid externalConnectionId, CancellationToken cancellationToken);
}

/// <summary>
/// Per-connection access-token cache — mirrors <see cref="Whoop.WhoopAccessTokenCache"/>
/// including the refresh-rotation race-rescue (Strava rotates refresh tokens
/// identically; the 2026-06-11 concurrency lesson applies verbatim). Strava
/// access tokens live ~6 hours; we cache for 5 minutes to bound staleness.
/// Singleton — the cache must outlive any one request.
///
/// One deliberate improvement over the WHOOP cache: refresh outcomes write
/// oauth_audit rows (token_refresh_success / token_refresh_failed), per the
/// Track D queue's S2 DoD. Transient failures don't audit — they'd flood the
/// table during a Strava outage without recording a state change.
/// </summary>
public sealed class StravaAccessTokenCache(
    IKeyVaultSecretsClient kv,
    IStravaOAuthClient strava,
    IServiceScopeFactory scopeFactory,
    ILogger<StravaAccessTokenCache> logger)
    : IStravaAccessTokenCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, CachedToken> _cache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<Result<string>> GetAccessTokenAsync(
        Guid externalConnectionId,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(externalConnectionId, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Result<string>.Success(cached.AccessToken);
        }

        // Single-flight: serialize concurrent misses for the same connection.
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(externalConnectionId, out cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return Result<string>.Success(cached.AccessToken);
            }

            return await RefreshAsync(externalConnectionId, cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<Result<string>> RefreshAsync(
        Guid externalConnectionId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == externalConnectionId, cancellationToken);

        if (connection is null)
        {
            return Result<string>.Failure($"External connection {externalConnectionId} not found.");
        }

        var refreshToken = await kv.TryGetSecretAsync(connection.KeyVaultSecretName, cancellationToken);
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Result<string>.Failure(
                $"Refresh token secret '{connection.KeyVaultSecretName}' is missing or empty.");
        }

        Result<StravaTokenResponse> refreshResult;
        try
        {
            refreshResult = await strava.RefreshTokenAsync(refreshToken, cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            || ex is TaskCanceledException tce && !cancellationToken.IsCancellationRequested && tce.InnerException is TimeoutException
            || ex is TimeoutException)
        {
            // Transient: Strava unreachable, 5xx, network/timeout. The refresh
            // token is still presumed valid — retry on the next pass. Do NOT
            // flip status, do NOT audit (see class doc), do NOT count failure.
            logger.LogWarning(ex,
                "Transient Strava refresh failure for connection {ConnectionId} — leaving status as-is",
                externalConnectionId);
            return Result<string>.Failure($"Transient Strava refresh failure: {ex.Message}");
        }

        if (!refreshResult.IsSuccess)
        {
            // Race-rescue before declaring permanent failure. Strava rotates
            // the refresh token on every call — when SomaCore.Api and
            // SomaCore.IngestionJobs refresh the same connection in the same
            // instant, the loser's RT is already dead and the winner has
            // written the new RT to Key Vault. Re-read KV and, if the RT
            // moved, retry exactly once with the fresh value. Full rationale
            // in WhoopAccessTokenCache.RefreshAsync.
            var rescueRt = await kv.TryGetSecretAsync(connection.KeyVaultSecretName, cancellationToken);
            if (!string.IsNullOrEmpty(rescueRt)
                && !string.Equals(rescueRt, refreshToken, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Refresh 4xx for connection {ConnectionId}; KV RT changed mid-flight — retrying once with the fresh RT",
                    externalConnectionId);
                try
                {
                    refreshResult = await strava.RefreshTokenAsync(rescueRt, cancellationToken);
                }
                catch (Exception ex) when (
                    ex is HttpRequestException
                    || ex is TaskCanceledException tce2 && !cancellationToken.IsCancellationRequested && tce2.InnerException is TimeoutException
                    || ex is TimeoutException)
                {
                    logger.LogWarning(ex,
                        "Race-rescue refresh hit a transient failure for connection {ConnectionId} — leaving status as-is",
                        externalConnectionId);
                    return Result<string>.Failure($"Transient Strava refresh failure (rescue): {ex.Message}");
                }
            }
        }

        if (!refreshResult.IsSuccess)
        {
            // Permanent: both the original attempt and (if applicable) the
            // race-rescue attempt returned 4xx. The user must re-authorize.
            connection.RefreshFailureCount += 1;
            connection.LastRefreshError = refreshResult.Error?.Length > 500
                ? refreshResult.Error[..500]
                : refreshResult.Error;
            connection.Status = ConnectionStatus.RefreshFailed;
            dbContext.OAuthAuditEntries.Add(new OAuthAuditEntry
            {
                UserId = connection.UserId,
                ExternalConnectionId = connection.Id,
                Source = OAuthAuditSource.Strava,
                Action = OAuthAuditAction.TokenRefreshFailed,
                Success = false,
                ErrorMessage = connection.LastRefreshError,
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Permanent refresh failure for connection {ConnectionId}: {Error}",
                externalConnectionId,
                refreshResult.Error);
            return Result<string>.Failure(refreshResult.Error!);
        }

        var token = refreshResult.Value!;

        // Rotate the refresh token in KV (Strava issues a new one on every refresh).
        await kv.SetSecretAsync(connection.KeyVaultSecretName, token.RefreshToken, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        connection.LastRefreshAt = now;
        connection.NextRefreshAt = now.AddSeconds(token.ExpiresInSeconds * 0.85);
        connection.RefreshFailureCount = 0;
        connection.LastRefreshError = null;
        if (connection.Status == ConnectionStatus.RefreshFailed)
        {
            connection.Status = ConnectionStatus.Active;
        }
        dbContext.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = connection.UserId,
            ExternalConnectionId = connection.Id,
            Source = OAuthAuditSource.Strava,
            Action = OAuthAuditAction.TokenRefreshSuccess,
            Success = true,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                expires_in_seconds = token.ExpiresInSeconds,
            })),
            OccurredAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var expiresAt = now.AddSeconds(token.ExpiresInSeconds).Subtract(TimeSpan.FromMinutes(1));
        var ttlExpiresAt = now.Add(CacheTtl);
        var bounded = expiresAt < ttlExpiresAt ? expiresAt : ttlExpiresAt;

        _cache[externalConnectionId] = new CachedToken(token.AccessToken, bounded);
        return Result<string>.Success(token.AccessToken);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}
