using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages.Admin;

/// <summary>
/// /admin/agent — list recent agent_invocations across all users, see input
/// snapshot + output side-by-side, force-regen (= delete) so the next /me
/// load triggers a fresh generation through the router.
///
/// Per ADR 0012 follow-up: this is the operational surface so we stop
/// reaching for docker'd psql every time someone sees a stale card.
/// </summary>
[Authorize(Policy = "Admin")]
public sealed class AgentModel(
    SomaCoreDbContext dbContext,
    ILogger<AgentModel> logger) : PageModel
{
    public IReadOnlyList<InvocationRow> Rows { get; private set; } = Array.Empty<InvocationRow>();
    public DateTimeOffset GeneratedAt { get; private set; }
    public string? Banner { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        [FromForm] Guid id,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.AgentInvocations
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (row is null)
        {
            Banner = $"Invocation {id} not found (already deleted?)";
        }
        else
        {
            dbContext.AgentInvocations.Remove(row);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Admin deleted agent_invocations row {InvocationId} for user {UserId} model={ModelId}",
                id, row.UserId, row.ModelId);
            Banner = $"Deleted invocation {id:N}. Next /me load for that user will trigger a fresh generation.";
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        GeneratedAt = DateTimeOffset.UtcNow;

        var raw = await dbContext.AgentInvocations
            .AsNoTracking()
            .Join(dbContext.Users.AsNoTracking(),
                  a => a.UserId,
                  u => u.Id,
                  (a, u) => new
                  {
                      a.Id, a.UserId, a.CreatedAt, a.ModelId, a.TodaysRead,
                      a.ActionsJson, a.InputSnapshot,
                      a.InputTokens, a.CachedInputTokens, a.OutputTokens,
                      a.CostEstimateUsd, a.DurationMs, a.ErrorMessage, a.TraceId,
                      UserEmail = u.Email,
                  })
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        Rows = raw
            .Select(x => new InvocationRow(
                Id: x.Id,
                UserId: x.UserId,
                UserEmail: x.UserEmail,
                CreatedAt: x.CreatedAt,
                ModelId: x.ModelId,
                IsStub: AgentInvocationKind.IsStub(x.ModelId),
                IsFailure: !string.IsNullOrEmpty(x.ErrorMessage),
                TodaysRead: x.TodaysRead,
                ActionsJson: x.ActionsJson.RootElement.GetRawText(),
                InputSnapshot: x.InputSnapshot.RootElement.GetRawText(),
                InputTokens: x.InputTokens,
                CachedInputTokens: x.CachedInputTokens,
                OutputTokens: x.OutputTokens,
                CostEstimateCents: x.CostEstimateUsd is null
                    ? null
                    : (int)(x.CostEstimateUsd.Value * 100m),
                DurationMs: x.DurationMs,
                ErrorMessage: x.ErrorMessage,
                TraceId: x.TraceId))
            .ToList();
    }

    public sealed record InvocationRow(
        Guid Id,
        Guid UserId,
        string UserEmail,
        DateTimeOffset CreatedAt,
        string ModelId,
        bool IsStub,
        bool IsFailure,
        string TodaysRead,
        string ActionsJson,
        string InputSnapshot,
        int? InputTokens,
        int? CachedInputTokens,
        int? OutputTokens,
        int? CostEstimateCents,
        int DurationMs,
        string? ErrorMessage,
        string? TraceId);
}
