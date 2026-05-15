using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;

namespace SomaCore.Api.Pages;

[Authorize]
public sealed class MeModel(
    SomaCoreDbContext dbContext,
    IRecoveryIngestionHandler recoveryHandler,
    ILogger<MeModel> logger,
    IAuthorizationService authorizationService) : PageModel
{
    /// <summary>
    /// On-open pull triggers when the latest recovery for the user is missing
    /// or older than this. Conservative — only kicks in for genuinely stale
    /// state (e.g., a webhook gave up after retries before reaching us).
    /// </summary>
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromHours(18);

    public bool IsAdmin { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public Guid? EntraOid { get; private set; }
    public Guid? SomaCoreUserId { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }

    public bool WhoopConnected { get; private set; }
    public long? WhoopUserId { get; private set; }
    public string? WhoopEmail { get; private set; }
    public DateTimeOffset? WhoopConnectedAt { get; private set; }
    public string? WhoopBanner { get; private set; }

    public RecoveryViewModel? LatestRecovery { get; private set; }

    public IReadOnlyList<RecoveryViewModel> RecentRecoveries { get; private set; } = Array.Empty<RecoveryViewModel>();

    /// <summary>True when this request forced an on-open pull (via <c>?force=true</c>).</summary>
    public bool ForcedOnOpenPull { get; private set; }

    public async Task OnGetAsync(
        [Microsoft.AspNetCore.Mvc.FromQuery] string? whoop,
        [Microsoft.AspNetCore.Mvc.FromQuery] bool? force,
        CancellationToken cancellationToken)
    {
        DisplayName = User.FindFirstValue("name") ?? "(no display name)";
        Email = User.FindFirstValue("preferred_username")
            ?? User.FindFirstValue(ClaimTypes.Email)
            ?? "(no email)";

        if (Guid.TryParse(User.GetObjectId(), out var entraOid))
        {
            EntraOid = entraOid;

            var user = await dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);

            if (user is not null)
            {
                SomaCoreUserId = user.Id;
                LastSeenAt = user.LastSeenAt;

                var connection = await dbContext.ExternalConnections
                    .AsNoTracking()
                    .Where(c => c.UserId == user.Id
                             && c.Source == ConnectionSource.Whoop
                             && c.Status == ConnectionStatus.Active)
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (connection is not null)
                {
                    WhoopConnected = true;
                    WhoopConnectedAt = connection.CreatedAt;
                    if (connection.ConnectionMetadata.RootElement.TryGetProperty("whoop_user_id", out var idEl)
                        && idEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        WhoopUserId = idEl.GetInt64();
                    }
                    if (connection.ConnectionMetadata.RootElement.TryGetProperty("whoop_email", out var emailEl)
                        && emailEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        WhoopEmail = emailEl.GetString();
                    }

                    var rows = await LoadRecentAsync(user.Id, cancellationToken);
                    RecentRecoveries = rows;
                    LatestRecovery = rows.FirstOrDefault();

                    // On-open pull: if we have no recovery on file OR the latest is stale,
                    // synchronously hit WHOOP via the shared ingestion handler. The page
                    // takes a slower load (3-5 sec) once, then renders the fresh data.
                    // `?force=true` overrides the staleness check so the pull is
                    // demonstrable on demand (admin-only — gated below).
                    var stale = LatestRecovery is null
                        || (DateTimeOffset.UtcNow - LatestRecovery.CycleStartAt) > StalenessThreshold;
                    var adminForce = force == true
                        && (await authorizationService.AuthorizeAsync(User, "Admin")).Succeeded;
                    if (stale || adminForce)
                    {
                        ForcedOnOpenPull = adminForce;
                        await TriggerOnOpenPullAsync(connection.Id, cancellationToken);
                        // Re-query after the pull so the view reflects the new state.
                        rows = await LoadRecentAsync(user.Id, cancellationToken);
                        RecentRecoveries = rows;
                        LatestRecovery = rows.FirstOrDefault();
                    }
                }
            }
        }

        WhoopBanner = whoop switch
        {
            "connected" => "WHOOP connected.",
            "failed"    => "WHOOP connection failed. Please try again.",
            "cancelled" => "WHOOP authorization was cancelled.",
            _           => null,
        };

        var adminCheck = await authorizationService.AuthorizeAsync(User, "Admin");
        IsAdmin = adminCheck.Succeeded;
    }

    private async Task<List<RecoveryViewModel>> LoadRecentAsync(Guid userId, CancellationToken ct) =>
        await dbContext.WhoopRecoveries
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CycleStartAt)
            .Take(14)
            .Select(r => new RecoveryViewModel(
                r.ScoreState,
                r.RecoveryScore,
                r.HrvRmssdMilli,
                r.RestingHeartRate,
                r.CycleStartAt,
                r.CycleEndAt,
                r.IngestedVia,
                r.IngestedAt))
            .ToListAsync(ct);

    private async Task TriggerOnOpenPullAsync(Guid connectionId, CancellationToken ct)
    {
        try
        {
            var request = new RecoveryIngestionRequest(
                ExternalConnectionId: connectionId,
                IngestedVia: IngestedVia.OnOpenPull,
                CycleId: null,
                SleepId: null,
                TraceId: HttpContext.TraceIdentifier);
            var result = await recoveryHandler.IngestAsync(request, ct);
            if (!result.IsSuccess)
            {
                logger.LogWarning("On-open pull failed for connection {ConnectionId}: {Error}",
                    connectionId, result.Error);
            }
        }
        catch (Exception ex)
        {
            // Don't block the page render on an on-open failure; the user sees
            // whatever was already in DB and the next webhook/poller fixes it.
            logger.LogWarning(ex, "On-open pull threw for connection {ConnectionId}", connectionId);
        }
    }

    public sealed record RecoveryViewModel(
        string ScoreState,
        int? RecoveryScore,
        decimal? HrvRmssdMilli,
        int? RestingHeartRate,
        DateTimeOffset CycleStartAt,
        DateTimeOffset? CycleEndAt,
        string IngestedVia,
        DateTimeOffset IngestedAt);
}
