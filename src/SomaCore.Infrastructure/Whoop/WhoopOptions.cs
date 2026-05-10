using System.ComponentModel.DataAnnotations;

namespace SomaCore.Infrastructure.Whoop;

public sealed class WhoopOptions
{
    public const string SectionName = "Whoop";

    [Required]
    public string ClientId { get; init; } = string.Empty;

    [Required]
    public string ClientSecret { get; init; } = string.Empty;

    [Required]
    [Url]
    public string RedirectUri { get; init; } = string.Empty;

    [Required]
    [Url]
    public string AuthorizeUri { get; init; } = "https://api.prod.whoop.com/oauth/oauth2/auth";

    [Required]
    [Url]
    public string TokenUri { get; init; } = "https://api.prod.whoop.com/oauth/oauth2/token";

    [Required]
    [Url]
    public string ProfileUri { get; init; } = "https://api.prod.whoop.com/developer/v2/user/profile/basic";

    /// <summary>Space-separated WHOOP scopes, e.g. "read:recovery read:cycles read:profile offline".</summary>
    [Required]
    public string Scopes { get; init; } = "read:recovery read:cycles read:profile offline";
}
