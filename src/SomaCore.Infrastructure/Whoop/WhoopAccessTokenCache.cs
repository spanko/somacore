using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;

namespace SomaCore.Infrastructure.Whoop;

public interface IWhoopAccessTokenCache
{
    /// <summary>
    /// Resolve a fresh access token for the given external_connection. On cache miss,
    /// reads the refresh token from KV, rotates via WHOOP, persists the new refresh
    /// token to KV, and updates connection metadata.
    /// </summary>
    Task<Result<string>> GetAccessTokenAsync(Guid externalConnectionId, CancellationToken cancellationToken);
}

/// <summary>
/// Per-connection access-token cache. Tokens live for ~1 hour at WHOOP; we cache
/// for 5 minutes to bound staleness, refresh proactively on miss. Per ADR 0007.
/// Singleton — the cache must outlive any one request.
/// </summary>
public sealed class WhoopAccessTokenCache(
    IKeyVaultSecretsClient kv,
    IWhoopOAuthClient whoop,
    IServiceScopeFactory scopeFactory,
    ILogger<WhoopAccessTokenCache> logger)
    : IWhoopAccessTokenCache
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

        Result<WhoopTokenResponse> refreshResult;
        try
        {
            refreshResult = await whoop.RefreshTokenAsync(refreshToken, cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            || ex is TaskCanceledException tce && !cancellationToken.IsCancellationRequested && tce.InnerException is TimeoutException
            || ex is TimeoutException)
        {
            // ┌─────────────────────────────────────────────────────────────────┐
            // │ Refresh failure mode: TRANSIENT.                                 │
            // │ WHOOP unreachable, 5xx, network/timeout. The refresh token is    │
            // │ still presumed valid — retry on the next sweep (every 30 min).   │
            // │ Do NOT flip status, do NOT increment refresh_failure_count.      │
            // │ The /me banner only kicks in for permanent failures, which keeps │
            // │ a temporary WHOOP outage from telling the user to reconnect.     │
            // └─────────────────────────────────────────────────────────────────┘
            logger.LogWarning(ex,
                "Transient refresh failure for connection {ConnectionId} — leaving status as-is",
                externalConnectionId);
            return Result<string>.Failure($"Transient WHOOP refresh failure: {ex.Message}");
        }

        if (!refreshResult.IsSuccess)
        {
            // ┌─────────────────────────────────────────────────────────────────┐
            // │ Race-rescue before declaring permanent failure.                  │
            // │                                                                  │
            // │ WHOOP rotates the refresh token on every call. Our two host      │
            // │ processes (SomaCore.Api + SomaCore.IngestionJobs) each keep      │
            // │ their own in-memory cache + per-process SemaphoreSlim. They      │
            // │ don't coordinate. When both processes need the same connection   │
            // │ in the same instant, both POST /oauth/oauth2/token with the     │
            // │ same RT — the first wins and rotates, the second sees a 4xx     │
            // │ because the RT it sent is now dead. That's a transient race,    │
            // │ not a permanent revocation: the OTHER caller has already        │
            // │ written the new RT to Key Vault.                                 │
            // │                                                                  │
            // │ Re-read KV and, if the RT has changed under us, retry exactly   │
            // │ once with the fresh value. If the rescue still fails OR the RT  │
            // │ in KV didn't move, it's a real permanent failure.                │
            // └─────────────────────────────────────────────────────────────────┘
            var rescueRt = await kv.TryGetSecretAsync(connection.KeyVaultSecretName, cancellationToken);
            if (!string.IsNullOrEmpty(rescueRt)
                && !string.Equals(rescueRt, refreshToken, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Refresh 4xx for connection {ConnectionId}; KV RT changed mid-flight — retrying once with the fresh RT",
                    externalConnectionId);
                try
                {
                    refreshResult = await whoop.RefreshTokenAsync(rescueRt, cancellationToken);
                }
                catch (Exception ex) when (
                    ex is HttpRequestException
                    || ex is TaskCanceledException tce2 && !cancellationToken.IsCancellationRequested && tce2.InnerException is TimeoutException
                    || ex is TimeoutException)
                {
                    logger.LogWarning(ex,
                        "Race-rescue refresh hit a transient failure for connection {ConnectionId} — leaving status as-is",
                        externalConnectionId);
                    return Result<string>.Failure($"Transient WHOOP refresh failure (rescue): {ex.Message}");
                }
            }
        }

        if (!refreshResult.IsSuccess)
        {
            // ┌─────────────────────────────────────────────────────────────────┐
            // │ Refresh failure mode: PERMANENT.                                 │
            // │ Both the original attempt and (if applicable) the race-rescue   │
            // │ attempt returned 4xx. The user must re-authorize.                │
            // │ Flip status to refresh_failed so /me renders the Reconnect       │
            // │ banner; the sweeper will keep no-op'ing on this row until the   │
            // │ user completes a fresh OAuth flow.                               │
            // │                                                                  │
            // │ This branch only fires for 4xx because WhoopOAuthClient throws   │
            // │ HttpRequestException on 5xx (handled in the catch above) and    │
            // │ returns Result.Failure only after seeing a 4xx response.        │
            // └─────────────────────────────────────────────────────────────────┘
            connection.RefreshFailureCount += 1;
            connection.LastRefreshError = refreshResult.Error?.Length > 500
                ? refreshResult.Error[..500]
                : refreshResult.Error;
            connection.Status = ConnectionStatus.RefreshFailed;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Permanent refresh failure for connection {ConnectionId}: {Error}",
                externalConnectionId,
                refreshResult.Error);
            return Result<string>.Failure(refreshResult.Error!);
        }

        var token = refreshResult.Value!;

        // Rotate the refresh token in KV (WHOOP issues a new one on every refresh).
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
        await dbContext.SaveChangesAsync(cancellationToken);

        var expiresAt = now.AddSeconds(token.ExpiresInSeconds).Subtract(TimeSpan.FromMinutes(1));
        var ttlExpiresAt = now.Add(CacheTtl);
        var bounded = expiresAt < ttlExpiresAt ? expiresAt : ttlExpiresAt;

        _cache[externalConnectionId] = new CachedToken(token.AccessToken, bounded);
        return Result<string>.Success(token.AccessToken);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}
