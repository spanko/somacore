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

    /// <summary>
    /// revokeUserOAuthAccess — DELETE on this URL with the user's current access
    /// token revokes their OAuth grant at WHOOP. WHOOP returns 204 on success.
    /// </summary>
    [Required]
    [Url]
    public string RevokeUri { get; init; } = "https://api.prod.whoop.com/developer/v2/user/access";

    /// <summary>Base URL for v2 user-data endpoints (cycles, recoveries).</summary>
    [Required]
    [Url]
    public string ApiBaseUri { get; init; } = "https://api.prod.whoop.com/developer/v2";

    /// <summary>Space-separated WHOOP scopes, e.g. "read:recovery read:cycles read:profile offline".</summary>
    [Required]
    public string Scopes { get; init; } = "read:recovery read:cycles read:profile offline";
}
