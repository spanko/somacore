using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Strava;

/// <summary>
/// The S3/S4 seam: fetch a Strava activity by id and upsert it into
/// strava_activities (summary always; detail per the >20-min policy).
/// S3's webhook drainer and S5's reconciliation poller both dispatch
/// through this interface; S4 supplies the real implementation.
/// </summary>
public interface IStravaActivityIngestService
{
    /// <summary>
    /// Fetch + upsert one activity. Idempotent on strava_activity_id.
    /// <paramref name="ingestedVia"/> is the trace-contract trigger
    /// ("webhook" / "poller" / "on_open_pull").
    /// </summary>
    Task<Result<StravaActivityIngestOutcome>> IngestAsync(
        Guid externalConnectionId,
        long stravaActivityId,
        string ingestedVia,
        string? traceId,
        CancellationToken cancellationToken);
}

public sealed record StravaActivityIngestOutcome(string Outcome);

/// <summary>
/// Placeholder until S4 lands. Returns Failure so the webhook_events row is
/// marked failed and stays visible; the S5 reconciliation poller re-ingests
/// anything missed once the real service exists. Unreachable in practice
/// today: Strava:Enabled is false and no webhook subscription is registered.
/// </summary>
public sealed class NotYetImplementedStravaActivityIngestService(
    ILogger<NotYetImplementedStravaActivityIngestService> logger)
    : IStravaActivityIngestService
{
    public Task<Result<StravaActivityIngestOutcome>> IngestAsync(
        Guid externalConnectionId,
        long stravaActivityId,
        string ingestedVia,
        string? traceId,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Strava activity ingest not yet implemented (S4); activity {StravaActivityId} for connection {ConnectionId} left for poller retry",
            stravaActivityId,
            externalConnectionId);
        return Task.FromResult(Result<StravaActivityIngestOutcome>.Failure(
            "Strava activity ingest service not yet implemented (S4)."));
    }
}
