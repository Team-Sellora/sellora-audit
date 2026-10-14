using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sellora.AuditService.Application.Audit;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Application.Reporting;
using Sellora.AuditService.Infrastructure.Audit;
using Sellora.AuditService.Infrastructure.Dependencies;
using Sellora.AuditService.Infrastructure.Ingestion;
using Sellora.AuditService.Infrastructure.Kafka;
using Sellora.AuditService.Infrastructure.Persistence;
using Sellora.AuditService.Infrastructure.Reporting;

namespace Sellora.AuditService.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Everything the audit service needs except the HTTP identity pieces.
    /// The runtime connection string is the audit_app role's; it can read
    /// and append, never migrate, update or delete audit entries. It has
    /// full DML on the reporting schema only.
    /// </summary>
    public static IServiceCollection AddAuditInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runBackgroundServices)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default")))
        {
            throw new InvalidOperationException("The audit database connection string is not configured (ConnectionStrings__Default).");
        }

        // Read when the context is built, so configuration added after service registration (test hosts) is honoured.
        services.AddDbContext<AuditDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IConfiguration>().GetConnectionString("Default"),
            AuditDatabase.ConfigureNpgsql));

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddAuditDependencies(configuration);

        services.Configure<AuditKafkaOptions>(configuration.GetSection(AuditKafkaOptions.SectionName));

        services.Configure<ReportingOptions>(configuration.GetSection(ReportingOptions.SectionName));

        services.AddScoped<IReportingProjector, ReportingProjector>();
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddScoped<IAuditMessageProcessor, AuditMessageProcessor>();

        services.AddScoped<IAuditReadService, AuditReadService>();
        services.AddScoped<IChainVerificationService, ChainVerificationService>();

        services.AddSingleton<ReportCache>();
        services.AddScoped<ReportQueryService>();
        services.AddScoped<IReportQueryService, CachedReportQueryService>();
        services.AddScoped<IProjectionRebuildService, ProjectionRebuildService>();

        if (runBackgroundServices)
        {
            services.AddHostedService<AuditEventConsumerService>();
        }

        return services;
    }
}
