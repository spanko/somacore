using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.Agent;

/// <summary>
/// One row per invocation of the SomaCore AI's daily card. We log every
/// call so we can review outputs, debug bad cards, and analyze what
/// patterns matter when Track B's rules engine is informed by what the
/// alpha surfaced.
///
/// Per ADR 0012, the daily card ships before the deterministic rules
/// engine. The input snapshot here is the raw signal we fed the model;
/// the output is what came back. Nothing in this table causes side
/// effects — it's a record of read-only recommendations.
/// </summary>
public class AgentInvocation : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// What we sent the model. Includes the input window (e.g. last 7 days
    /// of recovery/sleep/workout), the time-of-day context, and the persona
    /// + bounds version. Stored as jsonb so we can analyze input drift over
    /// time without parsing logs.
    /// </summary>
    public JsonDocument InputSnapshot { get; set; } = null!;

    /// <summary>
    /// The "today's read" paragraph as the model wrote it. Renders on /me
    /// in the agent's voice.
    /// </summary>
    public string TodaysRead { get; set; } = string.Empty;

    /// <summary>
    /// The structured actions the model returned. Persisted as jsonb so we
    /// can query against categories without deserializing the whole row.
    /// Matches the shape of <see cref="AgentAction"/> records in C#.
    /// </summary>
    public JsonDocument ActionsJson { get; set; } = null!;

    /// <summary>The Anthropic model id this call hit. Runtime value.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Cached input tokens this invocation hit, if any.</summary>
    public int? CachedInputTokens { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary>USD estimate at the time of the call. May drift from billing if pricing changes.</summary>
    public decimal? CostEstimateUsd { get; set; }

    public int DurationMs { get; set; }

    /// <summary>
    /// Null on success. On failure, carries the truncated error message so
    /// the admin review surface can show what happened without spelunking
    /// in Application Insights.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Correlation id for joining to Application Insights traces.</summary>
    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }
}
