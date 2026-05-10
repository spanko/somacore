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
