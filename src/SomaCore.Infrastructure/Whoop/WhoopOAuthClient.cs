using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Whoop;

public sealed class WhoopOAuthClient(
    HttpClient httpClient,
    IOptions<WhoopOptions> options,
    ILogger<WhoopOAuthClient> logger)
    : IWhoopOAuthClient
{
    private readonly WhoopOptions _options = options.Value;

    public string BuildAuthorizeUrl(string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"]     = _options.ClientId;
        query["redirect_uri"]  = _options.RedirectUri;
        query["response_type"] = "code";
        query["scope"]         = _options.Scopes;
        query["state"]         = state;
        return $"{_options.AuthorizeUri}?{query}";
    }

    public async Task<Result<WhoopTokenResponse>> ExchangeCodeAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = _options.RedirectUri,
            ["client_id"]     = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        return await PostTokenAsync(form, "code-exchange", cancellationToken);
    }

    public async Task<Result<WhoopTokenResponse>> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"]         = _options.Scopes,
            ["client_id"]     = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };
        return await PostTokenAsync(form, "refresh", cancellationToken);
    }

    public async Task<Result<WhoopBasicProfile>> GetBasicProfileAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.ProfileUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromResponseAsync<WhoopBasicProfile>(
                response,
                "profile-fetch",
                cancellationToken);
        }

        var profile = await response.Content.ReadFromJsonAsync<WhoopBasicProfile>(cancellationToken);
        return profile is null
            ? Result<WhoopBasicProfile>.Failure("WHOOP returned an empty profile body.")
            : Result<WhoopBasicProfile>.Success(profile);
    }

    private async Task<Result<WhoopTokenResponse>> PostTokenAsync(
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
            return await FailureFromResponseAsync<WhoopTokenResponse>(response, operation, cancellationToken);
        }

        var token = await response.Content.ReadFromJsonAsync<WhoopTokenResponse>(cancellationToken);
        if (token is null)
        {
            logger.LogError("WHOOP returned an empty body on {Operation}", operation);
            return Result<WhoopTokenResponse>.Failure($"WHOOP returned an empty body on {operation}.");
        }

        return Result<WhoopTokenResponse>.Success(token);
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
            "WHOOP {Operation} returned {StatusCode}: {Body}",
            operation,
            (int)response.StatusCode,
            truncated);
        return Result<T>.Failure($"WHOOP {operation} returned {(int)response.StatusCode}.");
    }
}
