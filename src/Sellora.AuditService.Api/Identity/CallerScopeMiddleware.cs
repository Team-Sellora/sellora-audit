using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Application.Dependencies;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Infrastructure.Dependencies;

namespace Sellora.AuditService.Api.Identity;

public sealed class CallerScopeOptions
{
    public const string Section = "CallerScope";

    public int CacheSeconds { get; set; } = 60;

    public int MissingProfileCacheSeconds { get; set; } = 30;
}

/// <summary>
/// Copied from sellora-order. Resolves an Area Manager's provinces from
/// Organization's GET /api/me/scope (cached per user), never from token
/// claims. Company Admins see their whole company and skip the lookup, so
/// they keep working when Organization is down. Only Company Admins and
/// Area Managers can use this service, so other roles are not looked up.
/// </summary>
public sealed class CallerScopeMiddleware(RequestDelegate next)
{
    public const string ItemKey = "sellora.caller-scope";

    public async Task InvokeAsync(
        HttpContext context,
        IOrganizationClient organization,
        IMemoryCache cache,
        IOptions<CallerScopeOptions> options,
        ILogger<CallerScopeMiddleware> logger)
    {
        var user = context.User;
        var role = RoleClaims.Broadest(user);

        if (user.Identity?.IsAuthenticated != true || role != SelloraRoles.AreaManager)
        {
            await next(context);
            return;
        }

        var key = $"caller-scope:{RoleClaims.CompanyId(user)}:{user.FindFirst("sub")?.Value}:{role}";

        if (!cache.TryGetValue(key, out CallerScope? scope))
        {
            try
            {
                var resolved = await organization.GetCallerScopeAsync(context.RequestAborted);
                scope = resolved ?? CallerScope.Empty;

                cache.Set(key, scope, TimeSpan.FromSeconds(resolved is null
                    ? options.Value.MissingProfileCacheSeconds
                    : options.Value.CacheSeconds));

                if (resolved is null)
                {
                    logger.LogWarning("No Organization profile for {Role} {Subject}; the caller sees nothing",
                        role, user.FindFirst("sub")?.Value);
                }
            }
            // Fail closed: an Area Manager whose provinces are unknown gets no answer rather than an unscoped one.
            catch (Exception exception) when (exception is DependencyUnavailableException or DependencyRejectedException)
            {
                logger.LogWarning(exception, "Could not resolve the caller's scope from Organization");

                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "30";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Dependency unavailable",
                    Detail = "Organization service is currently unavailable, so your provinces could not be resolved. Try again shortly.",
                    Extensions = { ["dependency"] = nameof(Dependency.Organization) }
                }, context.RequestAborted);
                return;
            }
        }

        context.Items[ItemKey] = scope;
        await next(context);
    }
}
