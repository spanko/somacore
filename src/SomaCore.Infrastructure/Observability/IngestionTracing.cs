using System.Diagnostics;

namespace SomaCore.Infrastructure.Observability;

/// <summary>
/// Implements the ingestion trace contract from ADR 0011. Every ingestion path
/// emits one root span (<c>{source}.ingestion</c>) with one or more child spans:
/// one or more upstream fetches and one handler span per invocation.
///
/// The contract is enforced by convention plus these helpers — not by inheritance.
/// Each helper wraps <see cref="ActivitySource.StartActivity"/> with the required
/// tags pre-applied so callers can't forget. Disposal is the caller's job via
/// <c>using</c>.
/// </summary>
public static class IngestionTracing
{
    /// <summary>
    /// Activity source name. Register with OpenTelemetry via
    /// <c>.AddSource(IngestionTracing.SourceName)</c> at process startup,
    /// and with an <see cref="ActivityListener"/> in tests.
    /// </summary>
    public const string SourceName = "SomaCore.Ingestion";

    public static readonly ActivitySource Source = new(SourceName);

    // Tag keys (ADR 0011, "The contract"). Centralized so misspellings are
    // a compile error rather than a silent dashboard miss.
    public static class Tags
    {
        public const string IngestionSource = "ingestion.source";
        public const string IngestionTrigger = "ingestion.trigger";
        public const string IngestionEventType = "ingestion.event_type";
        public const string ExternalConnectionId = "external_connection_id";
        public const string UpstreamTraceId = "trace_id";

        public const string HttpUrl = "http.url";
        public const string WhoopEndpoint = "whoop.endpoint";
        public const string WhoopCycleId = "whoop.cycle_id";
        public const string HttpStatusCode = "http.status_code";
        public const string FetchDurationMs = "fetch.duration_ms";

        public const string HandlerName = "handler.name";
        public const string HandlerOutcome = "handler.outcome";
        public const string EntityNaturalKey = "entity.natural_key";
        public const string ScoreState = "score_state";

        public const string OutcomesPrefix = "outcomes."; // outcomes.recovery, outcomes.sleep, outcomes.workout
    }

    public static class Outcomes
    {
        public const string Inserted = "Inserted";
        public const string Updated = "Updated";
        public const string NoOp = "NoOp";
        public const string SkippedNoData = "SkippedNoData";
        public const string NotInvoked = "NotInvoked";
    }

    /// <summary>
    /// Open the root ingestion span. Required tags from ADR 0011 are
    /// pre-applied. The span name follows <c>{source}.ingestion</c>.
    /// </summary>
    public static Activity? StartIngestionScope(
        string source,
        string trigger,
        string eventType,
        Guid externalConnectionId,
        string? upstreamTraceId)
    {
        var activity = Source.StartActivity($"{source}.ingestion", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(Tags.IngestionSource, source);
        activity.SetTag(Tags.IngestionTrigger, trigger);
        activity.SetTag(Tags.IngestionEventType, eventType);
        activity.SetTag(Tags.ExternalConnectionId, externalConnectionId.ToString());
        if (!string.IsNullOrEmpty(upstreamTraceId))
        {
            activity.SetTag(Tags.UpstreamTraceId, upstreamTraceId);
        }

        return activity;
    }

    /// <summary>
    /// Open an upstream-fetch child span. <paramref name="endpoint"/> is a
    /// stable logical name (e.g. <c>whoop.cycle.fetch</c>); <paramref name="naturalKey"/>
    /// is the natural key being fetched (e.g. the WHOOP cycle id as a string).
    /// HTTP-level outcome tags (<c>http.status_code</c>, <c>fetch.duration_ms</c>)
    /// should be set by the caller via <see cref="RecordFetchOutcome"/>.
    /// </summary>
    public static Activity? StartFetchScope(string endpoint, string? naturalKey)
    {
        var activity = Source.StartActivity(endpoint, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(Tags.WhoopEndpoint, endpoint);
        if (!string.IsNullOrEmpty(naturalKey))
        {
            activity.SetTag(Tags.WhoopCycleId, naturalKey);
        }
        return activity;
    }

    /// <summary>
    /// Set the fetch-completion tags. Call before disposing the fetch span.
    /// </summary>
    public static void RecordFetchOutcome(Activity? fetchSpan, int? httpStatusCode, long durationMs, string? httpUrl = null)
    {
        if (fetchSpan is null) return;
        if (httpStatusCode is int code) fetchSpan.SetTag(Tags.HttpStatusCode, code);
        fetchSpan.SetTag(Tags.FetchDurationMs, durationMs);
        if (!string.IsNullOrEmpty(httpUrl)) fetchSpan.SetTag(Tags.HttpUrl, httpUrl);
    }

    /// <summary>
    /// Open a handler child span. Each ingestion handler opens exactly one of
    /// these per invocation. The span name follows <c>{shortName}.ingest</c>
    /// (e.g. <c>recovery.ingest</c>, <c>sleep.ingest</c>).
    /// </summary>
    public static Activity? StartHandlerScope(string handlerShortName, string? naturalKey)
    {
        var activity = Source.StartActivity($"{handlerShortName}.ingest", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(Tags.HandlerName, $"{handlerShortName}_ingestion");
        if (!string.IsNullOrEmpty(naturalKey))
        {
            activity.SetTag(Tags.EntityNaturalKey, naturalKey);
        }
        return activity;
    }

    /// <summary>
    /// Set the handler-completion tags on the handler's own span.
    /// </summary>
    public static void RecordHandlerOutcome(Activity? handlerSpan, string outcome, string? scoreState = null)
    {
        if (handlerSpan is null) return;
        handlerSpan.SetTag(Tags.HandlerOutcome, outcome);
        if (!string.IsNullOrEmpty(scoreState))
        {
            handlerSpan.SetTag(Tags.ScoreState, scoreState);
        }
    }

    /// <summary>
    /// Set the rolled-up outcome tag on the root ingestion span (per ADR 0011).
    /// Called by each handler after completing, or by the orchestrator when
    /// a handler is short-circuited (in which case <paramref name="outcome"/>
    /// is <see cref="Outcomes.NotInvoked"/>).
    ///
    /// If <paramref name="rootSpan"/> is null, walks the current activity's
    /// parent chain to find the ingestion root (the one tagged with
    /// <see cref="Tags.IngestionSource"/>). This lets handlers record the
    /// outcome without having to be handed the root span explicitly.
    /// </summary>
    public static void RecordOutcome(Activity? rootSpan, string handlerShortName, string outcome)
    {
        var target = rootSpan ?? FindIngestionRoot(Activity.Current);
        target?.SetTag(Tags.OutcomesPrefix + handlerShortName, outcome);
    }

    private static Activity? FindIngestionRoot(Activity? from)
    {
        for (var a = from; a is not null; a = a.Parent)
        {
            if (a.GetTagItem(Tags.IngestionSource) is not null)
            {
                return a;
            }
        }
        return null;
    }
}
