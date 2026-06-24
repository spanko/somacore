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
/// If a live call fails (network, validation, anything), we DO NOT fall
/// back to the stub mid-render — the user opted in to seeing the live
/// agent, and silently downgrading would hide drift. We return the
/// Failure to the caller (<c>MeModel</c>) which logs and renders nothing.
/// </summary>
public sealed class DailyAgentRouter : IDailyAgentService
{
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
        return route switch
        {
            AgentRoute.Live when _live is not null
                => await _live.GetLatestAsync(userId, cancellationToken),
            _   => await _stub.GetLatestAsync(userId, cancellationToken),
        };
    }

    private async Task<AgentRoute> ResolveRouteAsync(Guid userId, CancellationToken ct)
    {
        if (!_options.Enabled || _live is null)
        {
            return AgentRoute.Stub;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var optedIn = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.AgentOptIn)
            .FirstOrDefaultAsync(ct);
        return optedIn ? AgentRoute.Live : AgentRoute.Stub;
    }

    private enum AgentRoute { Stub, Live }
}
