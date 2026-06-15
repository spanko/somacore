using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Api.Pages;

[Authorize]
public sealed class MeModel(
    SomaCoreDbContext dbContext,
    IRecoveryIngestionHandler recoveryHandler,
    ILogger<MeModel> logger,
    IAuthorizationService authorizationService,
    IOptions<WhoopOptions> whoopOptions,
    IDailyAgentService dailyAgent) : PageModel
{
    private readonly WhoopOptions _whoopOptions = whoopOptions.Value;

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
    public bool WhoopRefreshFailed { get; private set; }

    /// <summary>
    /// True when the connection's stored OAuth scopes are missing one or more
    /// scopes currently required by <see cref="WhoopOptions.Scopes"/>. Surfaces
    /// the silent-staleness case: refresh keeps succeeding under RFC 6749 §6
    /// (refresh cannot widen scope), connection looks healthy, but downstream
    /// fetches that need the new scopes will 403. Drives the same reconnect
    /// banner as <see cref="WhoopRefreshFailed"/>. Render-only; no status
    /// mutation.
    /// </summary>
    public bool WhoopScopesStale { get; private set; }

    /// <summary>True when the reconnect banner should render — either of the
    /// two render conditions above is enough.</summary>
    public bool WhoopNeedsReconnect => WhoopRefreshFailed || WhoopScopesStale;

    public long? WhoopUserId { get; private set; }
    public string? WhoopEmail { get; private set; }
    public DateTimeOffset? WhoopConnectedAt { get; private set; }
    public string? WhoopBanner { get; private set; }

    public RecoveryViewModel? LatestRecovery { get; private set; }

    public IReadOnlyList<RecoveryViewModel> RecentRecoveries { get; private set; } = Array.Empty<RecoveryViewModel>();

    /// <summary>
    /// The most recent daily-card agent response for this user. Null until
    /// the first invocation lands. Per ADR 0012, the card scaffolding
    /// renders from this; the Fable 5 backed implementation arrives later.
    /// </summary>
    public DailyAgentResponse? DailyCard { get; private set; }

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

                // Pick up Active OR RefreshFailed so the page can show the
                // Reconnect banner when WHOOP has hard-rejected our refresh
                // token. Revoked rows are ignored — they're already torn down.
                var connection = await dbContext.ExternalConnections
                    .AsNoTracking()
                    .Where(c => c.UserId == user.Id
                             && c.Source == ConnectionSource.Whoop
                             && (c.Status == ConnectionStatus.Active
                                 || c.Status == ConnectionStatus.RefreshFailed))
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (connection is not null)
                {
                    WhoopConnected = true;
                    WhoopRefreshFailed = connection.Status == ConnectionStatus.RefreshFailed;
                    WhoopScopesStale = !WhoopConnectionScopes.HasRequiredScopes(
                        connection.Scopes, _whoopOptions.GetRequiredScopes());
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

                    // Daily agent card per ADR 0012. Render whatever's freshest
                    // for this user; the stub currently auto-generates the
                    // first one if none exists so /me has something to show.
                    DailyCard = await dailyAgent.GetLatestAsync(user.Id, cancellationToken);
                    if (DailyCard is null)
                    {
                        var generate = await dailyAgent.GenerateAsync(user.Id, cancellationToken);
                        if (generate.IsSuccess)
                        {
                            DailyCard = generate.Value;
                        }
                        else
                        {
                            logger.LogWarning(
                                "Daily agent generate failed for user {UserId}: {Error}",
                                user.Id, generate.Error);
                        }
                    }

                    // On-open pull: if we have no recovery on file OR the latest is stale,
                    // synchronously hit WHOOP via the shared ingestion handler. The page
                    // takes a slower load (3-5 sec) once, then renders the fresh data.
                    // `?force=true` overrides the staleness check so the pull is
                    // demonstrable on demand (admin-only — gated below).
                    // Skip entirely when the connection is in refresh_failed: the
                    // refresh will just fail again and add latency for no win.
                    var stale = LatestRecovery is null
                        || (DateTimeOffset.UtcNow - LatestRecovery.CycleStartAt) > StalenessThreshold;
                    var adminForce = force == true
                        && (await authorizationService.AuthorizeAsync(User, "Admin")).Succeeded;
                    if (!WhoopRefreshFailed && (stale || adminForce))
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
            "connected"    => "WHOOP connected.",
            "failed"       => "WHOOP connection failed. Please try again.",
            "cancelled"    => "WHOOP authorization was cancelled.",
            "disconnected" => "WHOOP disconnected.",
            _              => null,
        };

        var adminCheck = await authorizationService.AuthorizeAsync(User, "Admin");
        IsAdmin = adminCheck.Succeeded;
    }

    // Recovery is joined to its underlying sleep two ways: (1) the recovery
    // row carries the WHOOP sleep UUID when WHOOP attributed one at score
    // time — that's the primary edge; (2) when WHOOP gave us a SCORED
    // recovery before the sleep row landed locally (race), we fall back to
    // a cycle-window overlap. The fallback runs only when the primary join
    // produces no row.
    private async Task<List<RecoveryViewModel>> LoadRecentAsync(Guid userId, CancellationToken ct)
    {
        var recoveries = await dbContext.WhoopRecoveries
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CycleStartAt)
            .Take(14)
            .Select(r => new
            {
                r.ScoreState,
                r.RecoveryScore,
                r.HrvRmssdMilli,
                r.RestingHeartRate,
                r.CycleStartAt,
                r.CycleEndAt,
                r.IngestedVia,
                r.IngestedAt,
                r.WhoopSleepId,
            })
            .ToListAsync(ct);

        if (recoveries.Count == 0)
        {
            return new List<RecoveryViewModel>();
        }

        var sleepIds = recoveries
            .Where(r => r.WhoopSleepId.HasValue)
            .Select(r => r.WhoopSleepId!.Value)
            .Distinct()
            .ToList();

        // A single WhoopSleepId can have multiple rows in whoop_sleeps when
        // the schema's unique index — (external_connection_id, whoop_sleep_id)
        // — doesn't catch it: e.g. an old sleep row carries NULL FK after a
        // disconnect (ON DELETE SET NULL), and a subsequent backfill on the
        // new connection ingests the same WHOOP sleep again under the new
        // FK. Both rows coexist. Materialize and dedupe in memory, keeping
        // the most recently ingested row for each id.
        var sleepRows = sleepIds.Count == 0
            ? new List<SleepDedupeRow>()
            : await dbContext.WhoopSleeps
                .AsNoTracking()
                .Where(s => s.UserId == userId && sleepIds.Contains(s.WhoopSleepId))
                .OrderByDescending(s => s.IngestedAt)
                .Select(s => new SleepDedupeRow(s.WhoopSleepId, s.StartAt, s.EndAt))
                .ToListAsync(ct);

        var sleepsByWhoopId = sleepRows
            .GroupBy(s => s.WhoopSleepId)
            .ToDictionary(g => g.Key, g =>
            {
                var first = g.First();
                return (first.StartAt, first.EndAt);
            });

        // Fallback: for recoveries with no WhoopSleepId, look for a sleep
        // that starts inside the recovery cycle window. Bounded query — only
        // runs if at least one recovery is missing a sleep id.
        var earliest = recoveries.Min(r => r.CycleStartAt);
        var latest   = recoveries.Max(r => r.CycleEndAt ?? r.CycleStartAt);

        var cycleSleeps = recoveries.Any(r => !r.WhoopSleepId.HasValue)
            ? await dbContext.WhoopSleeps
                .AsNoTracking()
                .Where(s => s.UserId == userId
                         && !s.Nap
                         && s.StartAt >= earliest
                         && s.StartAt <= latest)
                .Select(s => new { s.StartAt, s.EndAt })
                .ToListAsync(ct)
            : new();

        return recoveries.Select(r =>
        {
            DateTimeOffset? sleepStart = null;
            DateTimeOffset? sleepEnd   = null;

            if (r.WhoopSleepId.HasValue
                && sleepsByWhoopId.TryGetValue(r.WhoopSleepId.Value, out var match))
            {
                sleepStart = match.StartAt;
                sleepEnd   = match.EndAt;
            }
            else if (r.CycleEndAt is DateTimeOffset cycleEnd)
            {
                var fallback = cycleSleeps.FirstOrDefault(
                    s => s.StartAt >= r.CycleStartAt && s.StartAt <= cycleEnd);
                if (fallback is not null)
                {
                    sleepStart = fallback.StartAt;
                    sleepEnd   = fallback.EndAt;
                }
            }

            return new RecoveryViewModel(
                r.ScoreState,
                r.RecoveryScore,
                r.HrvRmssdMilli,
                r.RestingHeartRate,
                r.CycleStartAt,
                r.CycleEndAt,
                r.IngestedVia,
                r.IngestedAt,
                sleepStart,
                sleepEnd);
        }).ToList();
    }

    private sealed record SleepDedupeRow(Guid WhoopSleepId, DateTimeOffset StartAt, DateTimeOffset EndAt);

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
        DateTimeOffset IngestedAt,
        DateTimeOffset? SleepStartAt,
        DateTimeOffset? SleepEndAt);
}
