namespace SomaCore.Domain.Agent;

/// <summary>
/// Values for <see cref="AgentInvocation.Kind"/>. Distinct from the
/// Infrastructure-layer <c>AgentInvocationKind.IsStub</c> helper, which
/// discriminates stub vs live by model id — this discriminates what the
/// invocation was FOR.
/// </summary>
public static class AgentInvocationKinds
{
    public const string DailyCard = "daily_card";
    public const string QuickLogExtraction = "quick_log_extraction";
    public const string DocumentExtraction = "document_extraction";
    public const string Conversation = "conversation";
    public const string LabExtraction = "lab_extraction";
}
