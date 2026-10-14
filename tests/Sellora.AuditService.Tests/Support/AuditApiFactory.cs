using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Application.Dependencies;

namespace Sellora.AuditService.Tests.Support;

/// <summary>
/// The real API host in the "Testing" environment (no Kafka consumer), with
/// tokens replaced by <see cref="TestAuth"/> headers and Organization by
/// <see cref="OrganizationStub"/>. Without a connection string it points at
/// an unreachable database, for tests that must never reach one.
/// </summary>
public class AuditApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;

    public AuditApiFactory()
    {
    }

    protected AuditApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public OrganizationStub Organization { get; } = new();

    public HttpClient CreateClientAs(string? roles, Guid? companyId = null, string subject = "test-user")
    {
        var client = CreateClient();

        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuth.RolesHeader, roles);
            client.DefaultRequestHeaders.Add(TestAuth.SubjectHeader, subject);
        }

        if (companyId is { } company)
        {
            client.DefaultRequestHeaders.Add(TestAuth.CompanyHeader, company.ToString());
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = _connectionString
                ?? "Host=database.invalid;Database=audit_db;Username=audit_app;Password=unused;Timeout=2",
            ["Jwt:Authority"] = "https://auth.invalid",
            ["Jwt:MetadataAddress"] = "https://auth.invalid/.well-known/openid-configuration",
            ["Jwt:Issuer"] = "https://auth.invalid",
            ["Jwt:Audience:0"] = "test-audience",
            ["Dependencies:Organization:BaseUrl"] = "https://organization.invalid"
        }));

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuth.SchemeName;
                options.DefaultChallengeScheme = TestAuth.SchemeName;
                options.DefaultScheme = TestAuth.SchemeName;
            });
            services.AddSingleton<IOrganizationClient>(Organization);
        });
    }

    /// <summary>Signs a request in from headers, with the raw claim names WSO2 uses; no roles header means anonymous.</summary>
    public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string RolesHeader = "X-Test-Roles";
        public const string CompanyHeader = "X-Test-Company";
        public const string SubjectHeader = "X-Test-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(role => new Claim("roles", role.Trim()))
                .Append(new Claim("sub", Request.Headers[SubjectHeader].ToString()))
                .ToList();

            if (Request.Headers.TryGetValue(CompanyHeader, out var company))
            {
                claims.Add(new Claim("companyId", company.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
