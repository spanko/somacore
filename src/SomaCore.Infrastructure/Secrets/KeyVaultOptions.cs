using System.ComponentModel.DataAnnotations;

namespace SomaCore.Infrastructure.Secrets;

public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";

    [Required]
    [Url]
    public string VaultUri { get; init; } = string.Empty;
}
