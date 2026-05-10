using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages;

[Authorize]
public sealed class MeModel(
    SomaCoreDbContext dbContext,
    IAuthorizationService authorizationService) : PageModel
{
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

    public async Task OnGetAsync(
        [Microsoft.AspNetCore.Mvc.FromQuery] string? whoop,
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

                    var recovery = await dbContext.WhoopRecoveries
                        .AsNoTracking()
                        .Where(r => r.UserId == user.Id)
                        .OrderByDescending(r => r.CycleStartAt)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (recovery is not null)
                    {
                        LatestRecovery = new RecoveryViewModel(
                            recovery.ScoreState,
                            recovery.RecoveryScore,
                            recovery.HrvRmssdMilli,
                            recovery.RestingHeartRate,
                            recovery.CycleStartAt,
                            recovery.CycleEndAt,
                            recovery.IngestedVia,
                            recovery.IngestedAt);
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
