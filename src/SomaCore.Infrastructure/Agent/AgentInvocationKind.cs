namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Single source of truth for "is this stored invocation a stub or a real
/// model call?" Used by both <see cref="StubDailyAgentService"/> and
/// <see cref="LiveDailyAgentService"/> when materializing
/// <see cref="DailyAgentResponse"/> from a persisted row, so the
/// <c>IsStub</c> flag tells the truth regardless of which service is
/// holding the cursor right now.
///
/// The rule: any model id starting with <c>stub-</c> is a stub row. This
/// covers the current <c>stub-pre-ai</c> as well as historical
/// <c>stub-pre-fable</c> rows from earlier deploys.
/// </summary>
public static class AgentInvocationKind
{
    public static bool IsStub(string? modelId)
        => !string.IsNullOrEmpty(modelId)
        && modelId.StartsWith("stub-", System.StringComparison.Ordinal);
}
