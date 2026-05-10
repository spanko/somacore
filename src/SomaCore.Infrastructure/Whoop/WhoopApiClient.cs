using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Whoop;

public sealed class WhoopApiClient(
    HttpClient httpClient,
    IOptions<WhoopOptions> options,
    ILogger<WhoopApiClient> logger)
    : IWhoopApiClient
{
    private readonly WhoopOptions _options = options.Value;

    public async Task<Result<WhoopRecoveryPayload?>> GetRecoveryByCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/cycle/{cycleId}/recovery";
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<WhoopRecoveryPayload?>.Success(null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopRecoveryPayload?>(response, $"recovery-by-cycle/{cycleId}", cancellationToken);
        }

        var body = await response.Content.ReadFromJsonAsync<WhoopRecoveryPayload>(cancellationToken);
        return body is null
            ? Result<WhoopRecoveryPayload?>.Failure("WHOOP returned an empty recovery body.")
            : Result<WhoopRecoveryPayload?>.Success(body);
    }

    public async Task<Result<WhoopRecoveryListResponse>> ListRecentRecoveriesAsync(
        string accessToken,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/recovery?limit={limit}";
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopRecoveryListResponse>(response, "recovery-list", cancellationToken);
        }

        var body = await response.Content.ReadFromJsonAsync<WhoopRecoveryListResponse>(cancellationToken);
        return body is null
            ? Result<WhoopRecoveryListResponse>.Failure("WHOOP returned an empty list body.")
            : Result<WhoopRecoveryListResponse>.Success(body);
    }

    public async Task<Result<WhoopCyclePayload>> GetCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/cycle/{cycleId}";
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopCyclePayload>(response, $"cycle/{cycleId}", cancellationToken);
        }

        var body = await response.Content.ReadFromJsonAsync<WhoopCyclePayload>(cancellationToken);
        return body is null
            ? Result<WhoopCyclePayload>.Failure("WHOOP returned an empty cycle body.")
            : Result<WhoopCyclePayload>.Success(body);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<Result<T>> FailureFromAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
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
