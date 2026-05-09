using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Authentication;

public interface IUserProvisioningService
{
    Task<User?> EnsureUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed class UserProvisioningService(
    SomaCoreDbContext dbContext,
    ILogger<UserProvisioningService> logger)
    : IUserProvisioningService
{
    public async Task<User?> EnsureUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var oidString = principal.GetObjectId();
        var tidString = principal.GetTenantId();

        if (!Guid.TryParse(oidString, out var entraOid) ||
            !Guid.TryParse(tidString, out var entraTenantId))
        {
            logger.LogWarning(
                "Authenticated principal missing or malformed Entra OID/TID; skipping JIT provisioning. Oid={Oid} Tid={Tid}",
                oidString,
                tidString);
            return null;
        }

        var email = principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? string.Empty;
        var displayName = principal.FindFirstValue("name");
        var now = DateTimeOffset.UtcNow;

        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);

        if (user is null)
        {
            user = new User
            {
                EntraOid = entraOid,
                EntraTenantId = entraTenantId,
                Email = email,
                DisplayName = displayName,
                LastSeenAt = now,
            };
            dbContext.Users.Add(user);
            logger.LogInformation(
                "JIT-creating SomaCore user for Entra OID {EntraOid}",
                entraOid);
        }
        else
        {
            user.Email = email;
            user.DisplayName = displayName;
            user.LastSeenAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return user;
    }
}
