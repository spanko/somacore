using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.IngestionJobs.Jobs;

/// <summary>
/// Refreshes WHOOP access tokens that are within the lookahead window of
/// their next_refresh_at hint. Uses the same in-process token cache the
/// runtime uses, so a successful refresh:
///   - rotates the refresh token in Key Vault
///   - updates external_connections.{last,next}_refresh_at + clears
///     refresh_failure_count + flips status back to active if it was
///     refresh_failed
/// </summary>
public sealed class TokenRefreshSweeper(
    SomaCoreDbContext dbContext,
    IWhoopAccessTokenCache tokenCache,
    ILogger<TokenRefreshSweeper> logger)
    : IJob
{
    /// <summary>Refresh anything whose next_refresh_at falls within this window.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromMinutes(15);

    public string Name => JobName.TokenRefreshSweeper;

    public async Task<JobOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Lookahead);

        var due = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.Source == ConnectionSource.Whoop
                     && (c.Status == ConnectionStatus.Active || c.Status == ConnectionStatus.RefreshFailed)
                     && (c.NextRefreshAt == null || c.NextRefreshAt <= deadline))
            .Select(c => new { c.Id })
            .ToListAsync(cancellationToken);

        int refreshed = 0, failed = 0;
        string? firstError = null;

        foreach (var c in due)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var result = await tokenCache.GetAccessTokenAsync(c.Id, cancellationToken);
            if (result.IsSuccess)
            {
                refreshed++;
            }
            else
            {
                failed++;
                firstError ??= result.Error;
                logger.LogWarning("Sweep refresh failed for connection {ConnectionId}: {Error}", c.Id, result.Error);
            }
        }

        var success = failed == 0;
        return new JobOutcome(
            Success: success,
            Error: success ? null : firstError,
            Summary: new
            {
                due = due.Count,
                refreshed,
                failed,
                lookahead_minutes = (int)Lookahead.TotalMinutes,
            });
    }
}
