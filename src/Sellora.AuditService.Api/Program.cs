using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Sellora.AuditService.Api.Authorization;
using Sellora.AuditService.Api.Identity;
using Sellora.AuditService.Api.Middleware;
using Sellora.AuditService.Api.Tenancy;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Domain.Tenancy;
using Sellora.AuditService.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var jwt = builder.Configuration.GetSection("Jwt");
var audiences = jwt.GetSection("Audience").Get<string[]>() ?? new[] { jwt["Audience"]! };

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];
        options.MetadataAddress = jwt["MetadataAddress"]!;
        // Keep WSO2's raw claim names ("roles", "companyId", "sub"), as Order and Organization do.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = RoleClaims.ClaimType
        };

        if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
        {
            options.BackchannelHttpHandler = IdentityProviderBackchannel.CreateHandler(jwt["MetadataAddress"]);
        }
    });

builder.Services.AddAuthorization(options => options.AddSelloraRolePolicies());

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpTenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
builder.Services.AddScoped<ISystemTenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
builder.Services.AddScoped<IAccessTokenAccessor, HttpAccessTokenAccessor>();
builder.Services.AddScoped<ICorrelationIdAccessor, HttpCorrelationIdAccessor>();
builder.Services.Configure<CallerScopeOptions>(builder.Configuration.GetSection(CallerScopeOptions.Section));

// No MigrateAsync here: the runtime role (audit_app) has no DDL rights.
// CD applies migrations with the audit_owner credential first.
builder.Services.AddAuditInfrastructure(
    builder.Configuration,
    runBackgroundServices: !builder.Environment.IsEnvironment("Testing"));

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddControllers();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Containers receive internal HTTP; HTTPS is terminated by APIM / Azure.
if (!app.Environment.IsEnvironment("Container"))
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CallerScopeMiddleware>();

app.MapControllers();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (context, report) =>
        context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() })
}).WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));

await app.RunAsync();

public partial class Program
{
    protected Program()
    {
    }
}
