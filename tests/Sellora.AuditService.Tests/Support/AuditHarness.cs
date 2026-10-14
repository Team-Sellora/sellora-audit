using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Application.Ingestion;
using Sellora.AuditService.Infrastructure.Audit;
using Sellora.AuditService.Infrastructure.Ingestion;
using Sellora.AuditService.Infrastructure.Persistence;
using Sellora.AuditService.Infrastructure.Reporting;

namespace Sellora.AuditService.Tests.Support;

/// <summary>The real services wired over one runtime-role context, as one Kafka message or request sees them.</summary>
public sealed class AuditHarness : IAsyncDisposable
{
    public AuditHarness(PostgreSqlFixture fixture, Guid? tenantCompanyId = null)
    {
        Tenant = new TenantStub(tenantCompanyId);
        Db = fixture.CreateAppContext(Tenant);
        Projector = new ReportingProjector(Db, TimeProvider.System);
        Store = new AuditStore(Db, Tenant, Projector, TimeProvider.System, NullLogger<AuditStore>.Instance);
        Processor = new AuditMessageProcessor(Store);
        Verification = new ChainVerificationService(Db);
        Reports = new ReportQueryService(Db, Organization, Tokens, NullLogger<ReportQueryService>.Instance);
        Rebuild = new ProjectionRebuildService(
            Db, Tenant, Projector,
            new ReportCache(new MemoryCache(new MemoryCacheOptions()), Options.Create(new ReportingOptions())),
            TimeProvider.System, NullLogger<ProjectionRebuildService>.Instance);
    }

    public TenantStub Tenant { get; }

    public AuditDbContext Db { get; }

    public ReportingProjector Projector { get; }

    public AuditStore Store { get; }

    public AuditMessageProcessor Processor { get; }

    public ChainVerificationService Verification { get; }

    public OrganizationStub Organization { get; } = new();

    public TokenStub Tokens { get; } = new();

    public ReportQueryService Reports { get; }

    public ProjectionRebuildService Rebuild { get; }

    public async Task<ProcessingOutcome> IngestAsync(IncomingMessage message) =>
        await Processor.ProcessAsync(message, CancellationToken.None);

    public async Task IngestAllAsync(IEnumerable<IncomingMessage> messages)
    {
        foreach (var message in messages)
        {
            var outcome = await IngestAsync(message);
            Assert.NotEqual(ProcessingStatus.Rejected, outcome.Status);
        }
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
