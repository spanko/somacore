using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Whoop;

public interface IWhoopOAuthClient
{
    /// <summary>Build the WHOOP /authorize URL with state, scopes, and redirect_uri populated.</summary>
    string BuildAuthorizeUrl(string state);

    /// <summary>Exchange an authorization code for a token pair.</summary>
    Task<Result<WhoopTokenResponse>> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Refresh an existing token pair. Returns a new pair (refresh token may rotate).</summary>
    Task<Result<WhoopTokenResponse>> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>Fetch the basic profile for the user behind the access token.</summary>
    Task<Result<WhoopBasicProfile>> GetBasicProfileAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Revoke the user's OAuth grant at WHOOP via
    /// <c>DELETE /developer/v2/user/access</c> with the user's current access
    /// token as Bearer auth. WHOOP returns 204 on success. This is the
    /// public side of disconnect — callers should treat failure as best-effort
    /// and proceed with local teardown regardless.
    /// </summary>
    Task<Result<bool>> RevokeAccessAsync(string accessToken, CancellationToken cancellationToken);
}
