namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Loads Tai's persona + bounds docs from the published AgentDocs/ content.
/// Shared by the daily card and the coach conversation so both speak from
/// the same verbatim source. Cached after first read — the files only
/// change on deploy (container restart).
/// </summary>
internal static class AgentDocs
{
    private static readonly Lazy<(string Voice, string Bounds)> Cached = new(() =>
    {
        var docsDir = Path.Combine(AppContext.BaseDirectory, "AgentDocs");
        var voicePath = Path.Combine(docsDir, "agent-voice-and-persona.md");
        var boundsPath = Path.Combine(docsDir, "agent-bounds.md");

        if (!File.Exists(voicePath) || !File.Exists(boundsPath))
        {
            throw new InvalidOperationException(
                $"Agent docs not found at {docsDir}. " +
                "Expected Infrastructure.csproj Content items to publish " +
                "agent-voice-and-persona.md and agent-bounds.md to AgentDocs/.");
        }

        return (File.ReadAllText(voicePath), File.ReadAllText(boundsPath));
    });

    public static (string Voice, string Bounds) Load() => Cached.Value;
}
