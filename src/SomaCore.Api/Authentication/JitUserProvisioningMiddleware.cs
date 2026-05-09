namespace SomaCore.Api.Authentication;

/// <summary>
/// Ensures a row in <c>users</c> exists for the signed-in caller before the request
/// reaches the page/endpoint. One DB round-trip per authenticated request; we'll
/// add per-session caching once we measure the cost.
/// </summary>
public sealed class JitUserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IUserProvisioningService provisioning)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await provisioning.EnsureUserAsync(context.User, context.RequestAborted);
        }

        await next(context);
    }
}
