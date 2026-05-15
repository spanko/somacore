using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages.Admin;

[Authorize(Policy = "Admin")]
public sealed class HealthModel(SomaCoreDbContext dbContext) : PageModel
{
    public DateTimeOffset GeneratedAt { get; private set; }

    public WebhookSummary Webhooks { get; private set; } = new(0, 0, 0, 0, 0, null, null);
    public RecoverySummary Recoveries { get; private set; } = new(0, 0, 0, null);
    public IReadOnlyList<ConnectionRow> Connections { get; private set; } = Array.Empty<ConnectionRow>();
    public IReadOnlyList<JobRunRow> RecentJobRuns { get; private set; } = Array.Empty<JobRunRow>();
    public int DrainerQueueDepth { get; private set; }
    public DateTimeOffset? OldestUnprocessedReceivedAt { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        GeneratedAt = DateTimeOffset.UtcNow;
        var since24h = GeneratedAt.AddHours(-24);
        var since7d  = GeneratedAt.AddDays(-7);

        // ---- Webhook counts (last 24h) ----
        var wh24 = await dbContext.WebhookEvents
            .AsNoTracking()
            .Where(w => w.ReceivedAt >= since24h)
            .GroupBy(w => w.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync(cancellationToken);

        int n(string s) => wh24.FirstOrDefault(x => x.Status == s)?.N ?? 0;

        var lastReceived = await dbContext.WebhookEvents
            .AsNoTracking()
            .OrderByDescending(w => w.ReceivedAt)
            .Select(w => (DateTimeOffset?)w.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastProcessed = await dbContext.WebhookEvents
            .AsNoTracking()
            .Where(w => w.Status == WebhookEventStatus.Processed && w.ProcessedAt != null)
            .OrderByDescending(w => w.ProcessedAt)
            .Select(w => (DateTimeOffset?)w.ProcessedAt!.Value)
            .FirstOrDefaultAsync(cancellationToken);

        Webhooks = new WebhookSummary(
            Received: n(WebhookEventStatus.Received),
            Processing: n(WebhookEventStatus.Processing),
            Processed: n(WebhookEventStatus.Processed),
            Failed: n(WebhookEventStatus.Failed),
            Discarded: n(WebhookEventStatus.Discarded),
            LastReceivedAt: lastReceived,
            LastProcessedAt: lastProcessed);

        // ---- Drainer queue depth ----
        DrainerQueueDepth = await dbContext.WebhookEvents
            .AsNoTracking()
            .CountAsync(w => w.Status == WebhookEventStatus.Received
                          || w.Status == WebhookEventStatus.Processing, cancellationToken);

        OldestUnprocessedReceivedAt = await dbContext.WebhookEvents
            .AsNoTracking()
            .Where(w => w.Status == WebhookEventStatus.Received
                     || w.Status == WebhookEventStatus.Processing)
            .OrderBy(w => w.ReceivedAt)
            .Select(w => (DateTimeOffset?)w.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // ---- Recovery counts (last 7d) ----
        var rec7 = await dbContext.WhoopRecoveries
            .AsNoTracking()
            .Where(r => r.IngestedAt >= since7d)
            .GroupBy(r => r.ScoreState)
            .Select(g => new { State = g.Key, N = g.Count() })
            .ToListAsync(cancellationToken);

        int rec(string s) => rec7.FirstOrDefault(x => x.State == s)?.N ?? 0;

        var lastIngested = await dbContext.WhoopRecoveries
            .AsNoTracking()
            .OrderByDescending(r => r.IngestedAt)
            .Select(r => (DateTimeOffset?)r.IngestedAt)
            .FirstOrDefaultAsync(cancellationToken);

        Recoveries = new RecoverySummary(
            Scored: rec("SCORED"),
            Pending: rec("PENDING_SCORE"),
            Unscorable: rec("UNSCORABLE"),
            LastIngestedAt: lastIngested);

        // ---- Connection rows ----
        Connections = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.Source == ConnectionSource.Whoop)
            .OrderBy(c => c.Status)
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => new ConnectionRow(
                c.UserId,
                c.Status,
                c.LastRefreshAt,
                c.NextRefreshAt,
                c.RefreshFailureCount,
                c.LastRefreshError))
            .ToListAsync(cancellationToken);

        // ---- Recent job runs (last 5 per job) ----
        var jobNames = JobName.All.ToArray();
        var jobRows = new List<JobRunRow>();
        foreach (var jn in jobNames)
        {
            var rows = await dbContext.JobRuns
                .AsNoTracking()
                .Where(j => j.JobName == jn)
                .OrderByDescending(j => j.StartedAt)
                .Take(5)
                .Select(j => new JobRunRow(
                    j.JobName,
                    j.StartedAt,
                    j.EndedAt,
                    j.Success,
                    j.ErrorMessage))
                .ToListAsync(cancellationToken);
            jobRows.AddRange(rows);
        }
        RecentJobRuns = jobRows;
    }

    public sealed record WebhookSummary(
        int Received, int Processing, int Processed, int Failed, int Discarded,
        DateTimeOffset? LastReceivedAt, DateTimeOffset? LastProcessedAt);

    public sealed record RecoverySummary(
        int Scored, int Pending, int Unscorable, DateTimeOffset? LastIngestedAt);

    public sealed record ConnectionRow(
        Guid UserId,
        string Status,
        DateTimeOffset? LastRefreshAt,
        DateTimeOffset? NextRefreshAt,
        int RefreshFailureCount,
        string? LastRefreshError);

    public sealed record JobRunRow(
        string JobName,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        bool? Success,
        string? ErrorMessage);
}
