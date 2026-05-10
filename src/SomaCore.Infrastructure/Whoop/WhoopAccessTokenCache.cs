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

        var refreshResult = await whoop.RefreshTokenAsync(refreshToken, cancellationToken);
        if (!refreshResult.IsSuccess)
        {
            connection.RefreshFailureCount += 1;
            connection.LastRefreshError = refreshResult.Error?.Length > 500
                ? refreshResult.Error[..500]
                : refreshResult.Error;
            connection.Status = ConnectionStatus.RefreshFailed;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Refresh failed for connection {ConnectionId}: {Error}",
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
