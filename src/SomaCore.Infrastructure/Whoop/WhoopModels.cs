using System.Text.Json.Serialization;

namespace SomaCore.Infrastructure.Whoop;

public sealed record WhoopTokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")]    int ExpiresInSeconds,
    [property: JsonPropertyName("scope")]         string Scope,
    [property: JsonPropertyName("token_type")]    string TokenType);

public sealed record WhoopBasicProfile(
    [property: JsonPropertyName("user_id")]    long UserId,
    [property: JsonPropertyName("email")]      string Email,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")]  string? LastName);

/// <summary>
/// Inbound webhook envelope per WHOOP v2 docs. We only consume the subset we need;
/// the raw bytes are persisted in <c>webhook_events.raw_body</c> for replay.
/// </summary>
public sealed record WhoopWebhookEnvelope(
    [property: JsonPropertyName("user_id")]  long UserId,
    [property: JsonPropertyName("id")]       string Id,        // sleep UUID for v2 recovery events
    [property: JsonPropertyName("type")]     string EventType, // e.g. "recovery.updated"
    [property: JsonPropertyName("trace_id")] string TraceId);

/// <summary>
/// WHOOP v2 score sub-object on recovery. NB: WHOOP serializes <c>recovery_score</c>
/// and <c>resting_heart_rate</c> as JSON numbers with a trailing <c>.0</c> even though
/// the values are integer-valued. We type them as <c>decimal?</c> here so System.Text.Json
/// accepts the wire format, and round to int at the persistence boundary in
/// <see cref="SomaCore.Infrastructure.Recovery.RecoveryIngestionHandler"/>.
/// </summary>
public sealed record WhoopRecoveryScore(
    [property: JsonPropertyName("recovery_score")]      decimal? RecoveryScore,
    [property: JsonPropertyName("hrv_rmssd_milli")]     decimal? HrvRmssdMilli,
    [property: JsonPropertyName("resting_heart_rate")]  decimal? RestingHeartRate,
    [property: JsonPropertyName("spo2_percentage")]     decimal? Spo2Percentage,
    [property: JsonPropertyName("skin_temp_celsius")]   decimal? SkinTempCelsius,
    [property: JsonPropertyName("user_calibrating")]    bool? UserCalibrating);

public sealed record WhoopRecoveryPayload(
    [property: JsonPropertyName("cycle_id")]    long CycleId,
    [property: JsonPropertyName("sleep_id")]    Guid? SleepId,
    [property: JsonPropertyName("user_id")]    long UserId,
    [property: JsonPropertyName("created_at")]  DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")]  DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("score_state")] string ScoreState,
    [property: JsonPropertyName("score")]       WhoopRecoveryScore? Score);

/// <summary>Listing wrapper returned by GET /developer/v2/recovery.</summary>
public sealed record WhoopRecoveryListResponse(
    [property: JsonPropertyName("records")]    IReadOnlyList<WhoopRecoveryPayload> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);

/// <summary>WHOOP v2 cycle envelope (we only consume the time fields).</summary>
public sealed record WhoopCyclePayload(
    [property: JsonPropertyName("id")]       long Id,
    [property: JsonPropertyName("user_id")]  long UserId,
    [property: JsonPropertyName("start")]    DateTimeOffset Start,
    [property: JsonPropertyName("end")]      DateTimeOffset? End,
    [property: JsonPropertyName("score_state")] string ScoreState);

/// <summary>
/// WHOOP v2 sleep stage summary. All durations are milliseconds.
/// <c>total_sleep_time_milli</c> is derived (in-bed minus awake minus no-data);
/// WHOOP doesn't return it directly.
/// </summary>
public sealed record WhoopSleepStageSummary(
    [property: JsonPropertyName("total_in_bed_time_milli")]          long? TotalInBedTimeMilli,
    [property: JsonPropertyName("total_awake_time_milli")]           long? TotalAwakeTimeMilli,
    [property: JsonPropertyName("total_no_data_time_milli")]         long? TotalNoDataTimeMilli,
    [property: JsonPropertyName("total_light_sleep_time_milli")]     long? TotalLightSleepTimeMilli,
    [property: JsonPropertyName("total_slow_wave_sleep_time_milli")] long? TotalSlowWaveSleepTimeMilli,
    [property: JsonPropertyName("total_rem_sleep_time_milli")]       long? TotalRemSleepTimeMilli,
    [property: JsonPropertyName("sleep_cycle_count")]                int?  SleepCycleCount,
    [property: JsonPropertyName("disturbance_count")]                int?  DisturbanceCount);

/// <summary>
/// WHOOP v2 sleep score sub-object. Percentages are 0-100; WHOOP returns them
/// as JSON numbers that may carry a trailing <c>.0</c>, so we accept
/// <c>decimal?</c> on the wire (same pattern as <see cref="WhoopRecoveryScore"/>).
/// </summary>
public sealed record WhoopSleepScore(
    [property: JsonPropertyName("stage_summary")]                  WhoopSleepStageSummary? StageSummary,
    [property: JsonPropertyName("sleep_performance_percentage")]   decimal? SleepPerformancePercentage,
    [property: JsonPropertyName("sleep_consistency_percentage")]   decimal? SleepConsistencyPercentage,
    [property: JsonPropertyName("sleep_efficiency_percentage")]    decimal? SleepEfficiencyPercentage,
    [property: JsonPropertyName("respiratory_rate")]               decimal? RespiratoryRate);

/// <summary>
/// WHOOP v2 sleep envelope as returned by <c>GET /developer/v2/cycle/{id}/sleep</c>
/// (and also <c>GET /developer/v2/sleep/{id}</c>). The score sub-object is null
/// when <c>score_state</c> is anything other than <c>SCORED</c>.
/// </summary>
public sealed record WhoopSleepPayload(
    [property: JsonPropertyName("id")]              Guid Id,
    [property: JsonPropertyName("user_id")]         long UserId,
    [property: JsonPropertyName("created_at")]      DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")]      DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("start")]           DateTimeOffset Start,
    [property: JsonPropertyName("end")]             DateTimeOffset End,
    [property: JsonPropertyName("timezone_offset")] string TimezoneOffset,
    [property: JsonPropertyName("nap")]             bool Nap,
    [property: JsonPropertyName("score_state")]     string ScoreState,
    [property: JsonPropertyName("score")]           WhoopSleepScore? Score);

/// <summary>
/// Listing wrapper returned by <c>GET /developer/v2/activity/sleep</c>.
/// Cursor pagination via <c>next_token</c>; full sleep envelope inline so
/// Session 5 backfill avoids per-id refetches via the bypass handler.
/// </summary>
public sealed record WhoopSleepListResponse(
    [property: JsonPropertyName("records")]    IReadOnlyList<WhoopSleepPayload> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);

/// <summary>
/// WHOOP v2 workout score sub-object. WHOOP returns numeric fields with
/// arbitrary precision (e.g. <c>strain</c> 0..21 with several decimals;
/// <c>kilojoule</c> may also carry decimals); we accept everything as
/// <c>decimal?</c> on the wire and round to int where the schema demands it
/// (same pattern as <see cref="WhoopRecoveryScore"/>). The full WHOOP score
/// object (including unmapped fields like <c>zone_duration</c>,
/// <c>distance_meter</c>, <c>percent_recorded</c>) is preserved verbatim in
/// the <c>score</c> jsonb column for recomputability.
/// </summary>
public sealed record WhoopWorkoutScore(
    [property: JsonPropertyName("strain")]             decimal? Strain,
    [property: JsonPropertyName("average_heart_rate")] decimal? AverageHeartRate,
    [property: JsonPropertyName("max_heart_rate")]     decimal? MaxHeartRate,
    [property: JsonPropertyName("kilojoule")]          decimal? Kilojoule);

/// <summary>
/// WHOOP v2 workout envelope as returned by
/// <c>GET /developer/v2/activity/workout/{id}</c>. Note that workouts are
/// NOT cycle-keyed: there is no <c>cycle_id</c> here and no
/// <c>/cycle/{id}/workout</c> sub-endpoint. <c>sport_name</c> is v2's
/// string-typed replacement for the legacy <c>sport_id</c> integer.
/// </summary>
public sealed record WhoopWorkoutPayload(
    [property: JsonPropertyName("id")]              Guid Id,
    [property: JsonPropertyName("user_id")]         long UserId,
    [property: JsonPropertyName("created_at")]      DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")]      DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("start")]           DateTimeOffset Start,
    [property: JsonPropertyName("end")]             DateTimeOffset End,
    [property: JsonPropertyName("timezone_offset")] string TimezoneOffset,
    [property: JsonPropertyName("sport_name")]      string SportName,
    [property: JsonPropertyName("score_state")]     string ScoreState,
    [property: JsonPropertyName("score")]           WhoopWorkoutScore? Score);

/// <summary>
/// Listing wrapper returned by <c>GET /developer/v2/activity/workout</c>.
/// Cursor pagination via <c>next_token</c>; mirrors
/// <see cref="WhoopRecoveryListResponse"/>.
/// </summary>
public sealed record WhoopWorkoutListResponse(
    [property: JsonPropertyName("records")]    IReadOnlyList<WhoopWorkoutPayload> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);
