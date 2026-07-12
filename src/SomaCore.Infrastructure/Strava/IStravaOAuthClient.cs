using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Strava;

public interface IStravaOAuthClient
{
    /// <summary>Build the Strava /oauth/authorize URL with state, scopes, and redirect_uri populated.</summary>
    string BuildAuthorizeUrl(string state);

    /// <summary>
    /// Exchange an authorization code for a token pair. The response carries
    /// the athlete summary (id lands in connection_metadata as strava_athlete_id).
    /// </summary>
    Task<Result<StravaTokenResponse>> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Refresh an existing token pair. Returns a new pair (refresh token rotates on every call).</summary>
    Task<Result<StravaTokenResponse>> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>
    /// Revoke the user's OAuth grant at Strava via POST /oauth/deauthorize
    /// with the user's current access token. Best-effort — callers proceed
    /// with local teardown regardless of the outcome.
    /// </summary>
    Task<Result<bool>> DeauthorizeAsync(string accessToken, CancellationToken cancellationToken);
}
