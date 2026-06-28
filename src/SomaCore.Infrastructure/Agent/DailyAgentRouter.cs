using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Routes each <see cref="IDailyAgentService"/> call to either the
/// stub or the live (Anthropic-backed) implementation based on the
/// user's <c>users.agent_opt_in</c> flag and the global
/// <see cref="AnthropicOptions.Enabled"/> kill-switch.
///
/// Routing policy:
/// <list type="bullet">
///   <item><c>Anthropic.Enabled = false</c> → stub for everyone.</item>
///   <item><c>Anthropic.Enabled = true</c>, user opt-in = false → stub.</item>
///   <item><c>Anthropic.Enabled = true</c>, user opt-in = true → live.</item>
/// </list>
///
/// Every route decision is logged at Information level with the values
/// that drove it. When a user gets the wrong route in production
/// (e.g. opted-in user lands on stub), the log line tells us exactly
/// which branch fired — no more guessing.
///
/// <para>
/// <see cref="GetLatestAsync"/> applies two staleness rules on top of
/// whatever the underlying service returns:
/// <list type="bullet">
///   <item>If the latest stored row is older than <see cref="StaleAfter"/>,
///         return null so the caller triggers a fresh generation.</item>
///   <item>If the user is on the Live route but the latest stored row was
///         written by Stub (<c>IsStub == true</c>), return null. Without
///         this, an old stub row from before the opt-in flip blocks the
///         user from ever seeing a live card.</item>
/// </list>
/// </para>
///
/// If a live call fails (network, validation, anything), we DO NOT fall
/// back to the stub mid-render — the user opted in to seeing the live
/// agent, and silently downgrading would hide drift. We return the
/// Failure to the caller (<c>MeModel</c>) which logs and renders nothing.
/// </summary>
public sealed class DailyAgentRouter : IDailyAgentService
{
    // How long a daily card stays fresh before we regenerate. Tuned to
    // ~4 generations per user per day during the alpha (cheap with prompt
    // caching) so Adam and Tai can see iteration without manually deleting
    // rows. Lengthen once we have a "regenerate now" button.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private readonly StubDailyAgentService _stub;
    private readonly LiveDailyAgentService? _live;
    private readonly AnthropicOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyAgentRouter> _logger;

    public DailyAgentRouter(
        StubDailyAgentService stub,
        IOptions<AnthropicOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<DailyAgentRouter> logger,
        LiveDailyAgentService? live = null)
    {
        _stub = stub;
        _live = live;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<Result<DailyAgentResponse>> GenerateAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var route = await ResolveRouteAsync(userId, cancellationToken);
        return route switch
        {
            AgentRoute.Live when _live is not null
                => await _live.GenerateAsync(userId, cancellationToken),
            _   => await _stub.GenerateAsync(userId, cancellationToken),
        };
    }

    public async Task<DailyAgentResponse?> GetLatestAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var route = await ResolveRouteAsync(userId, cancellationToken);
        var latest = route switch
        {
            AgentRoute.Live when _live is not null
                => await _live.GetLatestAsync(userId, cancellationToken),
            _   => await _stub.GetLatestAsync(userId, cancellationToken),
        };

        if (latest is null)
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - latest.GeneratedAt;
        var stubBlockingLive = route == AgentRoute.Live && latest.IsStub;
        var ageStale = age > StaleAfter;

        if (stubBlockingLive || ageStale)
        {
            _logger.LogInformation(
                "Treating user {UserId}'s latest agent row as stale → forcing regen. " +
                "route={Route} ageMinutes={AgeMinutes} latestIsStub={IsStub} reason={Reason}",
                userId,
                route,
                (int)age.TotalMinutes,
                latest.IsStub,
                stubBlockingLive ? "stub-blocking-live" : "age-stale");
            return null;
        }

        return latest;
    }

    private async Task<AgentRoute> ResolveRouteAsync(Guid userId, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Routing user {UserId} to Stub: Anthropic.Enabled=false", userId);
            return AgentRoute.Stub;
        }
        if (_live is null)
        {
            _logger.LogWarning(
                "Routing user {UserId} to Stub: live service not registered despite Anthropic.Enabled=true. " +
                "Check that Anthropic:ApiKey was non-empty at startup.",
                userId);
            return AgentRoute.Stub;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var optedIn = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.AgentOptIn)
            .FirstOrDefaultAsync(ct);

        var route = optedIn ? AgentRoute.Live : AgentRoute.Stub;
        _logger.LogInformation(
            "Agent route resolved for user {UserId}: {Route} (anthropicEnabled={Enabled} liveRegistered={LiveRegistered} optedIn={OptedIn})",
            userId, route, _options.Enabled, _live is not null, optedIn);
        return route;
    }

    private enum AgentRoute { Stub, Live }
}
