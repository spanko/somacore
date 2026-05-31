using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Infrastructure.Backfill;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages.Admin;

[Authorize(Policy = "Admin")]
public sealed class BackfillModel(
    SomaCoreDbContext dbContext,
    IWhoopBackfillService backfillService,
    ILogger<BackfillModel> logger) : PageModel
{
    // Upper bound on the requested window so a typo doesn't pin WHOOP's API
    // for ten minutes. 90 days comfortably covers Tai's 30-day target plus
    // a fudge factor for edge cases (catch-up after extended downtime).
    public const int MaxDays = 90;

    public IReadOnlyList<ConnectionOption> Connections { get; private set; } = Array.Empty<ConnectionOption>();
    public DateTimeOffset GeneratedAt { get; private set; }

    [BindProperty]
    public Guid? SelectedConnectionId { get; set; }

    [BindProperty]
    [Range(1, MaxDays)]
    public int Days { get; set; } = 30;

    public BackfillSummary? LastSummary { get; private set; }
    public string? LastError { get; private set; }
    public ConnectionOption? LastTarget { get; private set; }
    public DateTimeOffset? LastStart { get; private set; }
    public DateTimeOffset? LastEnd { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadConnectionsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadConnectionsAsync(cancellationToken);

        if (SelectedConnectionId is null)
        {
            ModelState.AddModelError(nameof(SelectedConnectionId), "Pick a connection.");
        }
        if (Days < 1 || Days > MaxDays)
        {
            ModelState.AddModelError(nameof(Days), $"Days must be between 1 and {MaxDays}.");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        LastTarget = Connections.FirstOrDefault(c => c.ConnectionId == SelectedConnectionId!.Value);
        if (LastTarget is null)
        {
            ModelState.AddModelError(nameof(SelectedConnectionId),
                "Connection not found (or not an active WHOOP connection).");
            return Page();
        }

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-Days);
        LastStart = start;
        LastEnd = end;

        logger.LogInformation(
            "Admin backfill triggered connection={ConnectionId} days={Days} window=[{Start},{End}]",
            SelectedConnectionId!.Value, Days, start, end);

        var result = await backfillService.RunAsync(
            SelectedConnectionId!.Value, start, end, cancellationToken);

        if (result.IsSuccess)
        {
            LastSummary = result.Value;
        }
        else
        {
            LastError = result.Error;
        }

        return Page();
    }

    private async Task LoadConnectionsAsync(CancellationToken ct)
    {
        GeneratedAt = DateTimeOffset.UtcNow;

        // Project to an anonymous shape first, order in SQL, then construct
        // the record. EF can't translate OrderBy on a record-constructor
        // expression (the constructor isn't a column-bound projection).
        var rows = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.Source == ConnectionSource.Whoop
                     && c.Status == ConnectionStatus.Active)
            .Join(dbContext.Users.AsNoTracking(),
                  c => c.UserId,
                  u => u.Id,
                  (c, u) => new { c.Id, u.Email, u.DisplayName, c.CreatedAt })
            .OrderBy(x => x.Email)
            .ToListAsync(ct);

        Connections = rows
            .Select(x => new ConnectionOption(x.Id, x.Email, x.DisplayName, x.CreatedAt))
            .ToList();
    }

    public sealed record ConnectionOption(
        Guid ConnectionId,
        string Email,
        string? DisplayName,
        DateTimeOffset ConnectedAt);
}
