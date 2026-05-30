using System.Globalization;
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

    /// <summary>
    /// 429-retry budget (per request). Backoff schedule (without Retry-After):
    /// 1s, 2s, 4s. When WHOOP sends <c>Retry-After</c> we honor it instead.
    /// Cap retries at 3 — past that surface as Failure and let the caller
    /// decide (backfill re-runs are admin-triggered + idempotent).
    /// </summary>
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    };

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

    public Task<Result<WhoopRecoveryListResponse>> ListRecentRecoveriesAsync(
        string accessToken,
        int limit,
        CancellationToken cancellationToken)
        => ListRecoveriesAsync(accessToken, limit, nextToken: null, start: null, end: null, cancellationToken);

    public async Task<Result<WhoopRecoveryListResponse>> ListRecoveriesAsync(
        string accessToken,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken cancellationToken)
    {
        var url = BuildListUrl($"{_options.ApiBaseUri}/recovery", limit, nextToken, start, end);
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

    public async Task<Result<WhoopSleepPayload?>> GetSleepByCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/cycle/{cycleId}/sleep";
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<WhoopSleepPayload?>.Success(null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopSleepPayload?>(response, $"sleep-by-cycle/{cycleId}", cancellationToken);
        }

        return await ParseAsync<WhoopSleepPayload?>(response, $"sleep-by-cycle/{cycleId}", cancellationToken);
    }

    public async Task<Result<WhoopSleepListResponse>> ListSleepsAsync(
        string accessToken,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken cancellationToken)
    {
        var url = BuildListUrl($"{_options.ApiBaseUri}/activity/sleep", limit, nextToken, start, end);
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopSleepListResponse>(response, "sleep-list", cancellationToken);
        }

        return await ParseAsync<WhoopSleepListResponse>(response, "sleep-list", cancellationToken);
    }

    public async Task<Result<WhoopWorkoutPayload?>> GetWorkoutByIdAsync(
        string accessToken,
        Guid workoutId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/activity/workout/{workoutId}";
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<WhoopWorkoutPayload?>.Success(null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopWorkoutPayload?>(response, $"workout/{workoutId}", cancellationToken);
        }

        return await ParseAsync<WhoopWorkoutPayload?>(response, $"workout/{workoutId}", cancellationToken);
    }

    public Task<Result<WhoopWorkoutListResponse>> ListRecentWorkoutsAsync(
        string accessToken,
        int limit,
        string? nextToken,
        CancellationToken cancellationToken)
        => ListWorkoutsAsync(accessToken, limit, nextToken, start: null, end: null, cancellationToken);

    public async Task<Result<WhoopWorkoutListResponse>> ListWorkoutsAsync(
        string accessToken,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken cancellationToken)
    {
        var url = BuildListUrl($"{_options.ApiBaseUri}/activity/workout", limit, nextToken, start, end);
        using var response = await SendAsync(HttpMethod.Get, url, accessToken, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<WhoopWorkoutListResponse>(response, "workout-list", cancellationToken);
        }

        return await ParseAsync<WhoopWorkoutListResponse>(response, "workout-list", cancellationToken);
    }

    /// <summary>
    /// Compose a list-endpoint URL with optional pagination + date-window
    /// query params. WHOOP v2 expects ISO-8601 UTC for <c>start</c> and
    /// <c>end</c> (lowercase, with trailing Z).
    /// </summary>
    private static string BuildListUrl(
        string baseUrl,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        var url = $"{baseUrl}?limit={limit}";
        if (!string.IsNullOrEmpty(nextToken))
        {
            url += $"&nextToken={Uri.EscapeDataString(nextToken)}";
        }
        if (start is DateTimeOffset s)
        {
            url += $"&start={Uri.EscapeDataString(s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))}";
        }
        if (end is DateTimeOffset e)
        {
            url += $"&end={Uri.EscapeDataString(e.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))}";
        }
        return url;
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

    /// <summary>
    /// HTTP send with internal 429 retry. Honors <c>Retry-After</c> header
    /// when present (delta-seconds or HTTP-date); otherwise uses the
    /// fixed backoff schedule (1s, 2s, 4s). After <see cref="MaxRetries"/>
    /// the latest response is returned to the caller — it'll surface as a
    /// <c>Result.Failure</c> via <see cref="FailureFromAsync"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            // Each attempt needs a fresh HttpRequestMessage; HttpClient
            // disallows reuse. Dispose previous response so we don't leak.
            response?.Dispose();

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == MaxRetries)
            {
                return response;
            }

            var wait = ResolveBackoff(response, attempt);
            logger.LogWarning(
                "WHOOP rate-limited (429) for {Url}; retry {Attempt}/{Max} after {WaitMs}ms",
                url, attempt + 1, MaxRetries, (int)wait.TotalMilliseconds);
            await Task.Delay(wait, cancellationToken);
        }

        // Loop always returns inside; this is unreachable but the compiler doesn't know.
        return response!;
    }

    /// <summary>
    /// Compute the backoff wait for a 429. Prefer the server's
    /// <c>Retry-After</c> header (delta-seconds or HTTP-date) when present;
    /// otherwise fall back to the fixed schedule.
    /// </summary>
    private static TimeSpan ResolveBackoff(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter is RetryConditionHeaderValue retryAfter)
        {
            if (retryAfter.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return delta;
            }
            if (retryAfter.Date is DateTimeOffset until)
            {
                var wait = until - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) return wait;
            }
        }
        return BackoffSchedule[Math.Min(attempt, BackoffSchedule.Length - 1)];
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
