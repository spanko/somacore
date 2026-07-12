using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;
using SomaCore.Domain.StravaActivities;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Strava;

/// <summary>
/// Fetch + upsert one Strava activity (brief §1.5). Upsert keys on
/// strava_activity_id. The detail unit (hr_zones + splits + laps +
/// detail_fetched_at) is fetched synchronously only when
/// elapsed_seconds > <see cref="StravaOptions.DetailFetchMinSeconds"/>;
/// a detail-fetch failure logs a warning and leaves the summary row usable
/// with detail_fetched_at null — it never fails the ingest.
/// </summary>
public sealed class StravaActivityIngestService(
    SomaCoreDbContext dbContext,
    IStravaApiClient api,
    IStravaAccessTokenCache tokenCache,
    IOptions<StravaOptions> options,
    ILogger<StravaActivityIngestService> logger)
    : IStravaActivityIngestService
{
    public async Task<Result<StravaActivityIngestOutcome>> IngestAsync(
        Guid externalConnectionId,
        long stravaActivityId,
        string ingestedVia,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var connection = await dbContext.ExternalConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == externalConnectionId, cancellationToken);
        if (connection is null)
        {
            return Result<StravaActivityIngestOutcome>.Failure(
                $"External connection {externalConnectionId} not found.");
        }

        var token = await tokenCache.GetAccessTokenAsync(externalConnectionId, cancellationToken);
        if (!token.IsSuccess)
        {
            return Result<StravaActivityIngestOutcome>.Failure($"Token acquisition failed: {token.Error}");
        }

        using var handlerSpan = IngestionTracing.StartHandlerScope(
            "strava_activity", naturalKey: stravaActivityId.ToString());

        var fetchStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using var fetchSpan = IngestionTracing.StartFetchScope(
            "strava.activity.fetch", naturalKey: stravaActivityId.ToString());
        var fetched = await api.GetActivityAsync(token.Value!, stravaActivityId, cancellationToken);
        IngestionTracing.RecordFetchOutcome(
            fetchSpan,
            httpStatusCode: null,
            durationMs: (long)System.Diagnostics.Stopwatch.GetElapsedTime(fetchStart).TotalMilliseconds);

        if (!fetched.IsSuccess)
        {
            IngestionTracing.RecordHandlerOutcome(handlerSpan, "FetchFailed");
            return Result<StravaActivityIngestOutcome>.Failure(fetched.Error!);
        }

        if (fetched.Value is null)
        {
            // Deleted at Strava between the event and our fetch. The delete
            // webhook (or reconciliation) reconciles the local row; nothing
            // to upsert here.
            IngestionTracing.RecordHandlerOutcome(handlerSpan, IngestionTracing.Outcomes.SkippedNoData);
            return Result<StravaActivityIngestOutcome>.Success(
                new StravaActivityIngestOutcome(IngestionTracing.Outcomes.SkippedNoData));
        }

        var payload = fetched.Value.Payload;

        var existing = await dbContext.StravaActivities
            .FirstOrDefaultAsync(a => a.StravaActivityId == stravaActivityId, cancellationToken);

        var activity = existing ?? new StravaActivity
        {
            StravaActivityId = stravaActivityId,
            UserId = connection.UserId,
            ExternalConnectionId = externalConnectionId,
        };

        activity.StravaAthleteId = payload.Athlete?.Id ?? activity.StravaAthleteId;
        activity.ActivityType = payload.ResolvedType;
        activity.StartedAt = payload.StartDate;
        activity.ElapsedSeconds = payload.ElapsedTime;
        activity.MovingSeconds = payload.MovingTime;
        activity.DistanceMeters = payload.Distance;
        activity.TotalElevationGainM = payload.TotalElevationGain;
        activity.AverageSpeedMps = payload.AverageSpeed;
        activity.MaxSpeedMps = payload.MaxSpeed;
        activity.AverageHr = payload.AverageHeartrate is decimal avgHr ? (int)Math.Round(avgHr) : null;
        activity.MaxHr = payload.MaxHeartrate is decimal maxHr ? (int)Math.Round(maxHr) : null;
        activity.AverageCadence = payload.AverageCadence;
        activity.AverageWatts = payload.AverageWatts;
        activity.MaxWatts = payload.MaxWatts;
        activity.WeightedAvgWatts = payload.WeightedAverageWatts;
        activity.DeviceWatts = payload.DeviceWatts;
        activity.KudosCount = payload.KudosCount;
        activity.Calories = payload.Calories;
        activity.RawSummaryPayload = fetched.Value.Raw;
        activity.IngestedVia = ingestedVia;
        activity.IngestedAt = DateTimeOffset.UtcNow;
        activity.TraceId = traceId;

        if (existing is null)
        {
            dbContext.StravaActivities.Add(activity);
        }

        // Detail unit (§1.5): only worth an extra call for activities long
        // enough that zone/split structure informs coach reasoning.
        if (payload.ElapsedTime > options.Value.DetailFetchMinSeconds)
        {
            await TryFetchDetailAsync(activity, payload, token.Value!, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var outcome = existing is null
            ? IngestionTracing.Outcomes.Inserted
            : IngestionTracing.Outcomes.Updated;
        IngestionTracing.RecordHandlerOutcome(handlerSpan, outcome);

        logger.LogInformation(
            "Strava activity {StravaActivityId} {Outcome} for connection {ConnectionId} via {IngestedVia} (detail_fetched={DetailFetched})",
            stravaActivityId,
            outcome,
            externalConnectionId,
            ingestedVia,
            activity.DetailFetchedAt is not null);

        return Result<StravaActivityIngestOutcome>.Success(new StravaActivityIngestOutcome(outcome));
    }

    /// <summary>
    /// Fetch the zone distribution and stamp the detail unit. All-or-nothing:
    /// on failure the row keeps detail_fetched_at null so a later pass can
    /// re-attempt, and the ingest still succeeds — a missing zone breakdown
    /// must never cost us the activity itself.
    /// </summary>
    private async Task TryFetchDetailAsync(
        StravaActivity activity,
        StravaActivityPayload payload,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var fetchStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using var fetchSpan = IngestionTracing.StartFetchScope(
            "strava.activity.zones.fetch", naturalKey: activity.StravaActivityId.ToString());

        Result<JsonDocument?> zones;
        try
        {
            zones = await api.GetActivityZonesAsync(accessToken, activity.StravaActivityId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            logger.LogWarning(ex,
                "Strava detail fetch (zones) threw for activity {StravaActivityId} — summary kept, detail left for retry",
                activity.StravaActivityId);
            return;
        }
        finally
        {
            IngestionTracing.RecordFetchOutcome(
                fetchSpan,
                httpStatusCode: null,
                durationMs: (long)System.Diagnostics.Stopwatch.GetElapsedTime(fetchStart).TotalMilliseconds);
        }

        if (!zones.IsSuccess)
        {
            logger.LogWarning(
                "Strava detail fetch (zones) failed for activity {StravaActivityId}: {Error} — summary kept, detail left for retry",
                activity.StravaActivityId,
                zones.Error);
            return;
        }

        // Null zones = Strava has none for this activity (no HR data). Still
        // a completed detail pass — don't leave it looking retryable forever.
        activity.HrZones = zones.Value;
        activity.Splits = payload.SplitsMetric is JsonElement splits
            ? JsonDocument.Parse(splits.GetRawText())
            : null;
        activity.Laps = payload.Laps is JsonElement laps
            ? JsonDocument.Parse(laps.GetRawText())
            : null;
        activity.RawDetailPayload = zones.Value;
        activity.DetailFetchedAt = DateTimeOffset.UtcNow;
    }
}
