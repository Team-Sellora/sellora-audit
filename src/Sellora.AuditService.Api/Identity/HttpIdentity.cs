using System.Net.Security;
using Microsoft.Net.Http.Headers;
using Sellora.AuditService.Application.Identity;

namespace Sellora.AuditService.Api.Identity;

/// <summary>
/// Who is calling. Subject, role and tenant come from the validated token;
/// provinces come from Organization through <see cref="CallerScopeMiddleware"/>.
/// </summary>
public sealed class HttpCurrentUserContext(IHttpContextAccessor accessor) : ICurrentUserContext
{
    private static readonly string[] NameClaimTypes = { "name", "preferred_username", "username", "email" };

    private System.Security.Claims.ClaimsPrincipal? User => accessor.HttpContext?.User;

    private CallerScope Scope =>
        accessor.HttpContext?.Items[CallerScopeMiddleware.ItemKey] as CallerScope ?? CallerScope.Empty;

    public string? Subject => User?.FindFirst("sub")?.Value;

    public string? Name => NameClaimTypes
        .Select(type => User?.FindFirst(type)?.Value)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public string? Role => RoleClaims.Broadest(User);

    public Guid? CompanyId => RoleClaims.CompanyId(User);

    public IReadOnlyCollection<Guid> ProvinceIds => Scope.ProvinceIds;
}

public sealed class HttpAccessTokenAccessor(IHttpContextAccessor accessor) : IAccessTokenAccessor
{
    public string? GetBearerToken()
    {
        var header = accessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();

        return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}

/// <summary>
/// Copied from sellora-order. Local and staging hosts may not trust the
/// shared WSO2 CA; a certificate that fails validation is accepted only from
/// the identity provider's own host and port.
/// </summary>
public static class IdentityProviderBackchannel
{
    public static HttpClientHandler CreateHandler(string? metadataAddress)
    {
        Uri.TryCreate(metadataAddress, UriKind.Absolute, out var identityProvider);

        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                IsTrusted(request.RequestUri, errors, identityProvider)
        };
    }

    public static bool IsTrusted(Uri? requestUri, SslPolicyErrors errors, Uri? identityProvider)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return identityProvider is not null
            && requestUri is not null
            && string.Equals(requestUri.Host, identityProvider.Host, StringComparison.OrdinalIgnoreCase)
            && requestUri.Port == identityProvider.Port;
    }
}
