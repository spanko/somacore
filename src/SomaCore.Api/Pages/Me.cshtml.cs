using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages;

[Authorize]
public sealed class MeModel(SomaCoreDbContext dbContext) : PageModel
{
    public string DisplayName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public Guid? EntraOid { get; private set; }
    public Guid? SomaCoreUserId { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
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
            }
        }
    }
}
