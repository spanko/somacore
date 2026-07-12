using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Strava;

/// <summary>
/// Typed client for Strava's v3 data API — mirrors <see cref="Whoop.WhoopApiClient"/>'s
/// shape including the 429 retry with Retry-After honor. Strava's limits
/// (100 non-upload requests / 15 min on Standard tier) are far above our
/// three-user volume, but rate-limit handling is table stakes anyway.
/// </summary>
public sealed class StravaApiClient(
    HttpClient httpClient,
    IOptions<StravaOptions> options,
    ILogger<StravaApiClient> logger)
    : IStravaApiClient
{
    private readonly StravaOptions _options = options.Value;

    private const int MaxRetries = 3;
    private const int ListPageSize = 100;
    private const int MaxListPages = 5;

    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    };

    public async Task<Result<StravaActivityFetch?>> GetActivityAsync(
        string accessToken,
        long activityId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/activities/{activityId}";
        using var response = await SendAsync(url, accessToken, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<StravaActivityFetch?>.Success(null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<StravaActivityFetch?>(
                response, $"activity/{activityId}", cancellationToken);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var payload = JsonSerializer.Deserialize<StravaActivityPayload>(body);
            return payload is null
                ? Result<StravaActivityFetch?>.Failure($"Strava activity/{activityId} returned an empty body.")
                : Result<StravaActivityFetch?>.Success(new StravaActivityFetch(payload, JsonDocument.Parse(body)));
        }
        catch (JsonException ex)
        {
            LogParseFailure($"activity/{activityId}", body, ex);
            return Result<StravaActivityFetch?>.Failure(
                $"Strava activity/{activityId} JSON parse failed at {ex.Path}: {ex.Message}");
        }
    }

    public async Task<Result<JsonDocument?>> GetActivityZonesAsync(
        string accessToken,
        long activityId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUri}/activities/{activityId}/zones";
        using var response = await SendAsync(url, accessToken, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // No zone data for this activity — an answer, not an error.
            return Result<JsonDocument?>.Success(null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromAsync<JsonDocument?>(
                response, $"activity/{activityId}/zones", cancellationToken);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return Result<JsonDocument?>.Success(JsonDocument.Parse(body));
        }
        catch (JsonException ex)
        {
            LogParseFailure($"activity/{activityId}/zones", body, ex);
            return Result<JsonDocument?>.Failure(
                $"Strava activity/{activityId}/zones JSON parse failed: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<StravaActivityFetch>>> ListAthleteActivitiesAsync(
        string accessToken,
        long afterEpoch,
        CancellationToken cancellationToken)
    {
        var all = new List<StravaActivityFetch>();

        for (var page = 1; page <= MaxListPages; page++)
        {
            var url = $"{_options.ApiBaseUri}/athlete/activities" +
                      $"?after={afterEpoch}&page={page}&per_page={ListPageSize}";
            using var response = await SendAsync(url, accessToken, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return await FailureFromAsync<IReadOnlyList<StravaActivityFetch>>(
                    response, "athlete-activities", cancellationToken);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            List<StravaActivityPayload>? pageItems;
            try
            {
                pageItems = JsonSerializer.Deserialize<List<StravaActivityPayload>>(body);
            }
            catch (JsonException ex)
            {
                LogParseFailure("athlete-activities", body, ex);
                return Result<IReadOnlyList<StravaActivityFetch>>.Failure(
                    $"Strava athlete-activities JSON parse failed at {ex.Path}: {ex.Message}");
            }

            if (pageItems is null || pageItems.Count == 0)
            {
                break;
            }

            using var doc = JsonDocument.Parse(body);
            var elements = doc.RootElement.EnumerateArray().ToArray();
            for (var i = 0; i < pageItems.Count; i++)
            {
                all.Add(new StravaActivityFetch(
                    pageItems[i],
                    JsonDocument.Parse(elements[i].GetRawText())));
            }

            if (pageItems.Count < ListPageSize)
            {
                break;
            }
        }

        return Result<IReadOnlyList<StravaActivityFetch>>.Success(all);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            response?.Dispose();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == MaxRetries)
            {
                return response;
            }

            var wait = ResolveBackoff(response, attempt);
            logger.LogWarning(
                "Strava rate-limited (429) for {Url}; retry {Attempt}/{Max} after {WaitMs}ms",
                url, attempt + 1, MaxRetries, (int)wait.TotalMilliseconds);
            await Task.Delay(wait, cancellationToken);
        }

        return response!;
    }

    private static TimeSpan ResolveBackoff(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter is RetryConditionHeaderValue retryAfter)
        {
            if (retryAfter.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return delta;
            }
            if (retryAfter.Date is DateTimeOffset date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero)
                {
                    return until;
                }
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
            "Strava {Operation} returned {StatusCode}: {Body}",
            operation,
            (int)response.StatusCode,
            truncated);
        return Result<T>.Failure($"Strava {operation} returned {(int)response.StatusCode}.");
    }

    private void LogParseFailure(string operation, string body, JsonException ex)
    {
        var truncated = body.Length > 500 ? body[..500] : body;
        logger.LogError(ex,
            "Strava {Operation} JSON parse failed at {Path}: {Body}",
            operation,
            ex.Path,
            truncated);
    }
}
