using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Placeholder implementation that returns a hardcoded sample card so the
/// /me surface, the EF table, and the DI plumbing all work end to end.
///
/// Per ADR 0012 the real Fable 5 backed implementation lands in a separate
/// PR after Tai signs off on persona, in-bounds list, and the privacy doc.
/// Until then this stub gives us a visible card and a logged
/// <c>agent_invocations</c> row so we can develop the rendering surface
/// without burning any model spend.
///
/// Every response from the stub carries <c>IsStub = true</c>, which the
/// view uses to render a "scaffolding only" banner so we never accidentally
/// ship this to a real user as if it were the agent.
/// </summary>
public sealed class StubDailyAgentService(
    SomaCoreDbContext dbContext,
    ILogger<StubDailyAgentService> logger) : IDailyAgentService
{
    private const string StubModelId = "stub-pre-fable";

    public async Task<Result<DailyAgentResponse>> GenerateAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var actions = new List<AgentAction>
        {
            new("Sample action — drink 24 oz of water before your first meeting.",
                "Hydration is the cheapest performance lever and you're light on it from yesterday's late workout.",
                AgentActionCategory.Hydration,
                Rank: 1),
            new("Sample action — push your first coffee to 9:30 AM.",
                "Cortisol peaks naturally for the first 90 minutes after waking. Caffeine on top blunts the natural curve and steals from later.",
                AgentActionCategory.CaffeineTiming,
                Rank: 2),
            new("Sample action — keep today's workout to Zone 2.",
                "Recovery is on the low end of your trailing 7-day median; a high-strain session today compounds the deficit.",
                AgentActionCategory.WorkoutIntensity,
                Rank: 3),
        };

        var actionsJson = JsonSerializer.Serialize(actions);
        var inputSnapshot = JsonSerializer.Serialize(new
        {
            note = "stub — no real input window read yet",
            generatedAt,
        });

        var invocation = new AgentInvocation
        {
            UserId = userId,
            InputSnapshot = JsonDocument.Parse(inputSnapshot),
            TodaysRead = "Sample read — this is a scaffold of the daily card. The real Fable 5 voice lands in a separate PR after Tai signs off on persona + bounds + privacy.",
            ActionsJson = JsonDocument.Parse(actionsJson),
            ModelId = StubModelId,
            DurationMs = 0,
        };
        dbContext.AgentInvocations.Add(invocation);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Stub daily agent card generated for user {UserId} invocation {InvocationId}",
            userId, invocation.Id);

        return Result<DailyAgentResponse>.Success(new DailyAgentResponse(
            TodaysRead: invocation.TodaysRead,
            Actions: actions,
            GeneratedAt: generatedAt,
            ModelId: StubModelId,
            IsStub: true,
            CostEstimateCents: 0));
    }

    public async Task<DailyAgentResponse?> GetLatestAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var latest = await dbContext.AgentInvocations
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var actions = JsonSerializer.Deserialize<List<AgentAction>>(latest.ActionsJson.RootElement.GetRawText())
            ?? new List<AgentAction>();

        return new DailyAgentResponse(
            TodaysRead: latest.TodaysRead,
            Actions: actions,
            GeneratedAt: latest.CreatedAt,
            ModelId: latest.ModelId,
            IsStub: latest.ModelId == StubModelId,
            CostEstimateCents: latest.CostEstimateUsd is null
                ? null
                : (int)(latest.CostEstimateUsd.Value * 100m));
    }
}
