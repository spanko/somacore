using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Generates the daily-card recommendation for one user. Per ADR 0012 this
/// runs on Fable 5 (or a stub during scaffolding) and is the user-facing
/// surface for the agent's "today's read" + ranked actions.
///
/// Every successful invocation writes one row to <c>agent_invocations</c>
/// — that's the source of truth for what the agent has said and what it
/// cost. The /me page reads from there; this interface is the side-effecting
/// write path.
/// </summary>
public interface IDailyAgentService
{
    Task<Result<DailyAgentResponse>> GenerateAsync(
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read the most recent invocation for this user. Used by /me to render
    /// without forcing a fresh model call on every page load.
    /// </summary>
    Task<DailyAgentResponse?> GetLatestAsync(
        Guid userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shape of the daily card. The TodaysRead paragraph + ranked actions are
/// what renders on /me. Provenance fields surface in a "Why this" disclosure
/// so the user can always tell what the agent saw and where it came from.
/// </summary>
public sealed record DailyAgentResponse(
    string TodaysRead,
    IReadOnlyList<AgentAction> Actions,
    DateTimeOffset GeneratedAt,
    string ModelId,
    /// <summary>True when this response came from a placeholder stub, not a real model call. Surfaces a banner on the card.</summary>
    bool IsStub,
    int? CostEstimateCents);
