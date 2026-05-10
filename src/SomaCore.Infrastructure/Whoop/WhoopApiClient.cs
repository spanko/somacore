using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

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

        return await ParseAsync<WhoopRecoveryPayload?>(response, $"recovery-by-cycle/{cycleId}", cancellationToken);
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

        return await ParseAsync<WhoopRecoveryListResponse>(response, "recovery-list", cancellationToken);
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

        return await ParseAsync<WhoopCyclePayload>(response, $"cycle/{cycleId}", cancellationToken);
    }

    /// <summary>
    /// Read the response body as a string, then deserialize. On JsonException
    /// we log the first ~500 chars of the actual body so future debugging
    /// doesn't require capturing a live response.
    /// </summary>
    private async Task<Result<T>> ParseAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var value = JsonSerializer.Deserialize<T>(body);
            return value is null
                ? Result<T>.Failure($"WHOOP {operation} returned an empty body.")
                : Result<T>.Success(value);
        }
        catch (JsonException ex)
        {
            var truncated = body.Length > 500 ? body[..500] : body;
            logger.LogError(ex,
                "WHOOP {Operation} JSON parse failed at {Path}: {Body}",
                operation,
                ex.Path,
                truncated);
            return Result<T>.Failure(
                $"WHOOP {operation} JSON parse failed at {ex.Path}: {ex.Message}");
        }
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
