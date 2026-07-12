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
