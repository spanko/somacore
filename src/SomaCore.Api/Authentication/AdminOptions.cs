namespace SomaCore.Api.Authentication;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// Comma-separated list of Entra Object IDs that get the "Admin" authorization
    /// role. Phase-1 implementation: a static allowlist supplied via env var.
    /// Move to Entra app roles when external users exist.
    /// </summary>
    public string UserOids { get; init; } = string.Empty;

    public IReadOnlyCollection<Guid> ParseUserOids()
    {
        if (string.IsNullOrWhiteSpace(UserOids))
        {
            return Array.Empty<Guid>();
        }
        return UserOids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToArray();
    }
}
