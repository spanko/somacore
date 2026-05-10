using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Whoop;

/// <summary>
/// REST client for WHOOP user-data endpoints (recovery, cycles). OAuth-related
/// calls live on <see cref="IWhoopOAuthClient"/>.
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

    /// <summary>Fetch a cycle by id.</summary>
    Task<Result<WhoopCyclePayload>> GetCycleAsync(
        string accessToken,
        long cycleId,
        CancellationToken cancellationToken);
}
