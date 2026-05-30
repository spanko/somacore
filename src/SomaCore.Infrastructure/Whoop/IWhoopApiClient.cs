using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Whoop;

/// <summary>
/// REST client for WHOOP user-data endpoints (recovery, cycles, sleep,
/// workout). OAuth-related calls live on <see cref="IWhoopOAuthClient"/>.
///
/// All list endpoints support cursor pagination (<paramref name="nextToken"/>
/// = null for first page; response carries <c>NextToken</c> for the next
/// page). Date-window params (<paramref name="start"/>, <paramref name="end"/>)
/// are interpreted in UTC by WHOOP — see Session 5 post-session notes.
///
/// Transient 429 rate-limit responses are retried internally by the client
/// with exponential backoff plus <c>Retry-After</c> header honoring. Callers
/// see either eventual success or a <c>Result.Failure</c> after the retry
/// budget is exhausted.
/// </summary>
public interface IWhoopApiClient
{
    /// <summary>Fetch the recovery for a specific cycle. Returns null result if WHOOP returns 404.</summary>
    Task<Result<WhoopRecoveryPayload?>> GetRecoveryByCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken);

    /// <summary>List the most recent recoveries for the authenticated user.</summary>
    Task<Result<WhoopRecoveryListResponse>> ListRecentRecoveriesAsync(
        string accessToken,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// List recoveries with cursor pagination + optional UTC date window
    /// (Session 5 backfill). Pass <paramref name="nextToken"/> = null for
    /// the first page; the response's <c>NextToken</c> is null when no more
    /// pages exist.
    /// </summary>
    Task<Result<WhoopRecoveryListResponse>> ListRecoveriesAsync(
        string accessToken,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken cancellationToken);

    /// <summary>Fetch a cycle by id.</summary>
    Task<Result<WhoopCyclePayload>> GetCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetch the sleep object for a cycle. Returns <c>Success(null)</c> if
    /// WHOOP returns 404 — some cycles legitimately have no sleep block.
    /// </summary>
    Task<Result<WhoopSleepPayload?>> GetSleepByCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// List sleeps with cursor pagination + optional UTC date window.
    /// Session 5 confirmed WHOOP v2 exposes <c>GET /activity/sleep</c> with
    /// the same shape as the recovery and workout list endpoints
    /// (<c>{records, next_token}</c>, full envelope inline).
    /// </summary>
    Task<Result<WhoopSleepListResponse>> ListSleepsAsync(
        string accessToken,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetch a workout by id. Workouts are independent of cycles in WHOOP v2
    /// (their own endpoint <c>/activity/workout/{id}</c>). Returns
    /// <c>Success(null)</c> if WHOOP returns 404.
    /// </summary>
    Task<Result<WhoopWorkoutPayload?>> GetWorkoutByIdAsync(
        string accessToken,
        Guid workoutId,
        CancellationToken cancellationToken);

    /// <summary>
    /// List the most recent workouts for the authenticated user. Cursor
    /// pagination via <paramref name="nextToken"/>.
    /// </summary>
    Task<Result<WhoopWorkoutListResponse>> ListRecentWorkoutsAsync(
        string accessToken,
        int limit,
        string? nextToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// List workouts with cursor pagination + optional UTC date window
    /// (Session 5 backfill).
    /// </summary>
    Task<Result<WhoopWorkoutListResponse>> ListWorkoutsAsync(
        string accessToken,
        int limit,
        string? nextToken,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken cancellationToken);
}
