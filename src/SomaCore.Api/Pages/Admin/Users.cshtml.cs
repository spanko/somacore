using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages.Admin;

[Authorize(Policy = "Admin")]
public sealed class UsersModel(SomaCoreDbContext dbContext) : PageModel
{
    public IReadOnlyList<UserRow> Rows { get; private set; } = Array.Empty<UserRow>();
    public DateTimeOffset GeneratedAt { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        GeneratedAt = DateTimeOffset.UtcNow;

        Rows = await dbContext.Users
            .AsNoTracking()
            .Select(u => new UserRow
            {
                DisplayName = u.DisplayName,
                Email = u.Email,
                CreatedAt = u.CreatedAt,
                LastSeenAt = u.LastSeenAt,
                ActiveWhoopConnectedAt = u.ExternalConnections
                    .Where(c => c.Source == ConnectionSource.Whoop && c.Status == ConnectionStatus.Active)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => (DateTimeOffset?)c.CreatedAt)
                    .FirstOrDefault(),
                LastRefreshAt = u.ExternalConnections
                    .Where(c => c.Source == ConnectionSource.Whoop && c.Status == ConnectionStatus.Active)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => c.LastRefreshAt)
                    .FirstOrDefault(),
                LatestRecoveryCycleAt = u.WhoopRecoveries
                    .OrderByDescending(r => r.CycleStartAt)
                    .Select(r => (DateTimeOffset?)r.CycleStartAt)
                    .FirstOrDefault(),
                LatestRecoveryState = u.WhoopRecoveries
                    .OrderByDescending(r => r.CycleStartAt)
                    .Select(r => r.ScoreState)
                    .FirstOrDefault(),
                LatestRecoveryScore = u.WhoopRecoveries
                    .OrderByDescending(r => r.CycleStartAt)
                    .Select(r => r.RecoveryScore)
                    .FirstOrDefault(),
            })
            .OrderByDescending(r => r.LastSeenAt ?? r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public sealed class UserRow
    {
        public string? DisplayName { get; init; }
        public string Email { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public DateTimeOffset? ActiveWhoopConnectedAt { get; init; }
        public DateTimeOffset? LastRefreshAt { get; init; }
        public DateTimeOffset? LatestRecoveryCycleAt { get; init; }
        public string? LatestRecoveryState { get; init; }
        public int? LatestRecoveryScore { get; init; }
    }
}
