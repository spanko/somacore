using System.Net.Http.Json;
using System.Web;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Strava;

public sealed class StravaOAuthClient(
    HttpClient httpClient,
    IOptions<StravaOptions> options,
    ILogger<StravaOAuthClient> logger)
    : IStravaOAuthClient
{
    private readonly StravaOptions _options = options.Value;

    public string BuildAuthorizeUrl(string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = _options.ClientId;
        query["redirect_uri"] = _options.RedirectUri;
        query["response_type"] = "code";
        query["approval_prompt"] = "auto";
        query["scope"] = _options.Scopes;
        query["state"] = state;
        return $"{_options.AuthorizeUri}?{query}";
    }

    public async Task<Result<StravaTokenResponse>> ExchangeCodeAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        return await PostTokenAsync(form, "code-exchange", cancellationToken);
    }

    public async Task<Result<StravaTokenResponse>> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        return await PostTokenAsync(form, "refresh", cancellationToken);
    }

    public async Task<Result<bool>> DeauthorizeAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.DeauthorizeUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["access_token"] = accessToken,
            }),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);

        // 200 with the revoked token echoed back per developers.strava.com.
        if (response.IsSuccessStatusCode)
        {
            return Result<bool>.Success(true);
        }

        // Same semantics as WhoopOAuthClient.RevokeAccessAsync: throw on 5xx,
        // Result.Failure on 4xx — either way the caller tears down locally.
        return await FailureFromResponseAsync<bool>(response, "deauthorize", cancellationToken);
    }

    private async Task<Result<StravaTokenResponse>> PostTokenAsync(
        Dictionary<string, string> form,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUri)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromResponseAsync<StravaTokenResponse>(response, operation, cancellationToken);
        }

        var token = await response.Content.ReadFromJsonAsync<StravaTokenResponse>(cancellationToken);
        if (token is null)
        {
            logger.LogError("Strava returned an empty body on {Operation}", operation);
            return Result<StravaTokenResponse>.Failure($"Strava returned an empty body on {operation}.");
        }

        return Result<StravaTokenResponse>.Success(token);
    }

    private async Task<Result<T>> FailureFromResponseAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        // 5xx is unexpected: throw so the caller's retry / error path kicks in.
        if ((int)response.StatusCode >= 500)
        {
            response.EnsureSuccessStatusCode();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var truncated = body.Length > 500 ? body[..500] : body;
        logger.LogWarning(
            "Strava {Operation} returned {StatusCode}: {Body}",
            operation,
            (int)response.StatusCode,
            truncated);
        return Result<T>.Failure($"Strava {operation} returned {(int)response.StatusCode}.");
    }
}
