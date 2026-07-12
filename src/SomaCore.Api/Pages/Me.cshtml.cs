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
using SomaCore.Infrastructure.QuickLog;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Strava;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Api.Pages;

[Authorize]
public sealed class MeModel(
    SomaCoreDbContext dbContext,
    IRecoveryIngestionHandler recoveryHandler,
    ILogger<MeModel> logger,
    IAuthorizationService authorizationService,
    IOptions<WhoopOptions> whoopOptions,
    IDailyAgentService dailyAgent,
    IQuickLogExtractionService quickLogExtraction,
    IQuickLogEntryService quickLogEntries,
    IOptions<QuickLogOptions> quickLogOptions,
    IOptions<StravaOptions> stravaOptions) : PageModel
{
    private readonly WhoopOptions _whoopOptions = whoopOptions.Value;
    private readonly QuickLogOptions _quickLogOptions = quickLogOptions.Value;
    private readonly StravaOptions _stravaOptions = stravaOptions.Value;

    private static readonly System.Text.Json.JsonSerializerOptions DraftJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

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

    // ------------------------------------------------------------------
    // Strava (session-strava-integration.md; Track D Session 3)
    // ------------------------------------------------------------------

    /// <summary>Strava card renders only when Strava:Enabled — false by
    /// default and false in dev until Adam flips it at deploy time.</summary>
    public bool StravaEnabled => _stravaOptions.Enabled;

    public bool StravaConnected { get; private set; }
    public bool StravaRefreshFailed { get; private set; }
    public long? StravaAthleteId { get; private set; }
    public string? StravaUsername { get; private set; }
    public DateTimeOffset? StravaConnectedAt { get; private set; }
    public string? StravaBanner { get; private set; }

    public RecoveryViewModel? LatestRecovery { get; private set; }

    public IReadOnlyList<RecoveryViewModel> RecentRecoveries { get; private set; } = Array.Empty<RecoveryViewModel>();

    /// <summary>
    /// The most recent daily-card agent response for this user. Null until
    /// the first invocation lands. Per ADR 0012, the card scaffolding
    /// renders from this; the network-backed implementation arrives later.
    /// </summary>
    public DailyAgentResponse? DailyCard { get; private set; }

    /// <summary>True when this request forced an on-open pull (via <c>?force=true</c>).</summary>
    public bool ForcedOnOpenPull { get; private set; }

    // ------------------------------------------------------------------
    // Quick-log (session-quick-log.md)
    // ------------------------------------------------------------------

    /// <summary>Quick-log surface renders only when QuickLog:Enabled (Tai's privacy Part 4 gate).</summary>
    public bool QuickLogEnabled => _quickLogOptions.Enabled;

    public int QuickLogMaxChars => _quickLogOptions.MaxInputChars;

    /// <summary>The extraction awaiting the user's Confirm, carried across the PRG redirect via TempData.</summary>
    public QuickLogExtraction? PendingDraft { get; private set; }

    /// <summary>Serialized <see cref="PendingDraft"/> for the confirm form's hidden field.</summary>
    public string? PendingDraftJson { get; private set; }

    /// <summary>The user's original line — round-tripped so Edit can refill the input box.</summary>
    public string? PendingSourceText { get; private set; }

    /// <summary>Pre-filled input value (set by the Edit action).</summary>
    public string? QuickLogText { get; private set; }

    public string? QuickLogBanner { get; private set; }

    public string? QuickLogError { get; private set; }

    public IReadOnlyList<LoggedItem> LoggedItems { get; private set; } = Array.Empty<LoggedItem>();

    public async Task OnGetAsync(
        [Microsoft.AspNetCore.Mvc.FromQuery] string? whoop,
        [Microsoft.AspNetCore.Mvc.FromQuery] string? strava,
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
            "connected" => "WHOOP connected.",
            "failed" => "WHOOP connection failed. Please try again.",
            "cancelled" => "WHOOP authorization was cancelled.",
            "disconnected" => "WHOOP disconnected.",
            _ => null,
        };

        if (StravaEnabled)
        {
            await LoadStravaConnectionAsync(cancellationToken);
            StravaBanner = strava switch
            {
                "connected" => "Strava connected.",
                "failed" => "Strava connection failed. Please try again.",
                "cancelled" => "Strava authorization was cancelled.",
                "disconnected" => "Strava disconnected.",
                _ => null,
            };
        }

        var adminCheck = await authorizationService.AuthorizeAsync(User, "Admin");
        IsAdmin = adminCheck.Succeeded;

        if (QuickLogEnabled && SomaCoreUserId is { } uid)
        {
            LoggedItems = await quickLogEntries.GetLoggedItemsAsync(uid, cancellationToken);

            // Pending draft + banners arrive across the PRG redirect.
            if (TempData["QuickLogDraft"] is string draftJson)
            {
                try
                {
                    PendingDraft = System.Text.Json.JsonSerializer
                        .Deserialize<QuickLogExtraction>(draftJson, DraftJsonOptions);
                    PendingDraftJson = draftJson;
                    PendingSourceText = TempData["QuickLogSourceText"] as string;
                }
                catch (System.Text.Json.JsonException)
                {
                    PendingDraft = null;
                }
            }
            QuickLogText = TempData["QuickLogText"] as string;
            QuickLogBanner = TempData["QuickLogBanner"] as string;
            QuickLogError = TempData["QuickLogError"] as string;
        }
    }

    // ------------------------------------------------------------------
    // Quick-log handlers. PRG throughout: every POST redirects back to /me
    // and the pending draft / banners ride TempData. Nothing persists
    // without the explicit Confirm post (privacy Part 4 / ADR 0012's
    // no-autonomous-action commitment).
    // ------------------------------------------------------------------

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnPostQuickLogAsync(
        string? quickLogText, CancellationToken cancellationToken)
    {
        var userId = await ResolveUserIdAsync(cancellationToken);
        if (userId is null || !QuickLogEnabled)
        {
            return RedirectToPage();
        }

        var text = (quickLogText ?? "").Trim();
        var extraction = await quickLogExtraction.ExtractAsync(userId.Value, text, cancellationToken);

        if (!extraction.IsSuccess)
        {
            TempData["QuickLogError"] = extraction.Error;
            TempData["QuickLogText"] = text;
            return RedirectToPage();
        }

        if (extraction.Value!.EntryType == QuickLogEntryType.Unclassified)
        {
            TempData["QuickLogError"] = extraction.Value.Message
                ?? "I couldn't tell what that was — try a meal, workout, or note.";
            TempData["QuickLogText"] = text;
            return RedirectToPage();
        }

        TempData["QuickLogDraft"] = System.Text.Json.JsonSerializer
            .Serialize(extraction.Value, DraftJsonOptions);
        TempData["QuickLogSourceText"] = text;
        return RedirectToPage();
    }

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnPostQuickLogConfirmAsync(
        string? draftJson, CancellationToken cancellationToken)
    {
        var userId = await ResolveUserIdAsync(cancellationToken);
        if (userId is null || !QuickLogEnabled || string.IsNullOrWhiteSpace(draftJson))
        {
            return RedirectToPage();
        }

        QuickLogExtraction? draft;
        try
        {
            draft = System.Text.Json.JsonSerializer
                .Deserialize<QuickLogExtraction>(draftJson, DraftJsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            draft = null;
        }
        if (draft is null)
        {
            TempData["QuickLogError"] = "That entry couldn't be read back — try logging it again.";
            return RedirectToPage();
        }

        // Re-validated inside ConfirmAsync — the hidden field is client
        // input and gets the same range checks as a fresh extraction.
        var result = await quickLogEntries.ConfirmAsync(
            userId.Value, draft, HttpContext.TraceIdentifier, cancellationToken);

        if (result.IsSuccess)
        {
            TempData["QuickLogBanner"] = result.Value;
        }
        else
        {
            TempData["QuickLogError"] = result.Error;
        }
        return RedirectToPage();
    }

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnPostQuickLogEditAsync(
        string? sourceText, CancellationToken cancellationToken)
    {
        // "Edit" = refill the input with the original line so the user can
        // rephrase. The draft is dropped; nothing was persisted.
        var userId = await ResolveUserIdAsync(cancellationToken);
        if (userId is not null && QuickLogEnabled)
        {
            TempData["QuickLogText"] = sourceText ?? "";
        }
        return RedirectToPage();
    }

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnPostQuickLogDeleteAsync(
        string itemType, Guid itemId, CancellationToken cancellationToken)
    {
        var userId = await ResolveUserIdAsync(cancellationToken);
        if (userId is not null && QuickLogEnabled)
        {
            var result = await quickLogEntries.DeleteAsync(userId.Value, itemType, itemId, cancellationToken);
            TempData["QuickLogBanner"] = result.IsSuccess ? "Deleted." : result.Error;
        }
        return RedirectToPage();
    }

    private async Task<Guid?> ResolveUserIdAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.GetObjectId(), out var entraOid))
        {
            return null;
        }
        var id = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.EntraOid == entraOid)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
        return id;
    }

    // Recovery is joined to its underlying sleep two ways: (1) the recovery
    // row carries the WHOOP sleep UUID when WHOOP attributed one at score
    // time — that's the primary edge; (2) when WHOOP gave us a SCORED
    // recovery before the sleep row landed locally (race), we fall back to
    // a cycle-window overlap. The fallback runs only when the primary join
    // produces no row.
    /// <summary>
    /// Populate the Strava card state — mirrors the WHOOP connection load:
    /// Active OR RefreshFailed rows surface (the latter drives the reconnect
    /// banner); Revoked rows are already torn down and ignored.
    /// </summary>
    private async Task LoadStravaConnectionAsync(CancellationToken cancellationToken)
    {
        if (SomaCoreUserId is not { } userId)
        {
            return;
        }

        var connection = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.UserId == userId
                     && c.Source == ConnectionSource.Strava
                     && (c.Status == ConnectionStatus.Active
                         || c.Status == ConnectionStatus.RefreshFailed))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (connection is null)
        {
            return;
        }

        StravaConnected = true;
        StravaRefreshFailed = connection.Status == ConnectionStatus.RefreshFailed;
        StravaConnectedAt = connection.CreatedAt;
        if (connection.ConnectionMetadata.RootElement.TryGetProperty("strava_athlete_id", out var athleteEl)
            && athleteEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            StravaAthleteId = athleteEl.GetInt64();
        }
        if (connection.ConnectionMetadata.RootElement.TryGetProperty("strava_username", out var usernameEl)
            && usernameEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            StravaUsername = usernameEl.GetString();
        }
    }

    private async Task<List<RecoveryViewModel>> LoadRecentAsync(Guid userId, CancellationToken ct)
    {
        // A single WhoopCycleId can have multiple rows in whoop_recoveries —
        // the unique index is on (external_connection_id, whoop_cycle_id), so
        // the same WHOOP cycle gets a fresh row every time a disconnect +
        // reconnect + backfill cycle runs (old connection's row, NULL-FK row
        // from the cascade, new connection's row). Without deduping, the
        // 14 most recent recovery rows are dominated by duplicates of the
        // last few cycle dates instead of 14 distinct cycles.
        //
        // Pull more than 14 raw rows, then dedupe in memory by
        // whoop_cycle_id keeping the most recently ingested row. 42 = 14
        // distinct cycles × 3 worst-case duplicates per cycle (the pattern
        // we saw after the 2026-06-22 mass-reconnect: old connection +
        // NULL-FK + new connection).
        var rawRows = await dbContext.WhoopRecoveries
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CycleStartAt)
            .ThenByDescending(r => r.IngestedAt)
            .Take(42)
            .Select(r => new
            {
                r.WhoopCycleId,
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

        var recoveries = rawRows
            .GroupBy(r => r.WhoopCycleId)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderByDescending(r => r.CycleStartAt)
            .Take(14)
            .ToList();

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
        var latest = recoveries.Max(r => r.CycleEndAt ?? r.CycleStartAt);

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
            DateTimeOffset? sleepEnd = null;

            if (r.WhoopSleepId.HasValue
                && sleepsByWhoopId.TryGetValue(r.WhoopSleepId.Value, out var match))
            {
                sleepStart = match.StartAt;
                sleepEnd = match.EndAt;
            }
            else if (r.CycleEndAt is DateTimeOffset cycleEnd)
            {
                var fallback = cycleSleeps.FirstOrDefault(
                    s => s.StartAt >= r.CycleStartAt && s.StartAt <= cycleEnd);
                if (fallback is not null)
                {
                    sleepStart = fallback.StartAt;
                    sleepEnd = fallback.EndAt;
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
