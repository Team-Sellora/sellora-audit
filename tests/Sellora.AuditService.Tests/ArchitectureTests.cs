using System.Reflection;
using ApiMarker = Program;
using ApplicationMarker = Sellora.AuditService.Application.Ingestion.AuditEnvelopeNormaliser;
using DomainMarker = Sellora.AuditService.Domain.Audit.AuditEntry;
using InfrastructureMarker = Sellora.AuditService.Infrastructure.Persistence.AuditDbContext;

namespace Sellora.AuditService.Tests;

public class ArchitectureTests
{
    private const string Domain = "Sellora.AuditService.Domain";
    private const string Application = "Sellora.AuditService.Application";
    private const string Infrastructure = "Sellora.AuditService.Infrastructure";
    private const string Api = "Sellora.AuditService.Api";

    [Fact]
    public void Domain_references_no_other_service_project()
    {
        Assert.Empty(ServiceReferences(typeof(DomainMarker).Assembly));
    }

    [Fact]
    public void Application_references_only_domain()
    {
        var references = ServiceReferences(typeof(ApplicationMarker).Assembly);

        Assert.All(references, name => Assert.Equal(Domain, name));
    }

    [Fact]
    public void Domain_and_application_do_not_depend_on_ef_core_kafka_or_aspnet()
    {
        var forbidden = new[] { "Microsoft.EntityFrameworkCore", "Confluent.Kafka", "Microsoft.AspNetCore", "Npgsql" };

        var offenders = new[] { typeof(DomainMarker).Assembly, typeof(ApplicationMarker).Assembly }
            .SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name!)
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Infrastructure_does_not_reference_api()
    {
        var references = ServiceReferences(typeof(InfrastructureMarker).Assembly);

        Assert.DoesNotContain(Api, references);
    }

    [Fact]
    public void Project_names_match_assembly_names()
    {
        Assert.Equal(Domain, typeof(DomainMarker).Assembly.GetName().Name);
        Assert.Equal(Application, typeof(ApplicationMarker).Assembly.GetName().Name);
        Assert.Equal(Infrastructure, typeof(InfrastructureMarker).Assembly.GetName().Name);
        Assert.Equal(Api, typeof(ApiMarker).Assembly.GetName().Name);
    }

    private static string[] ServiceReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("Sellora.AuditService.", StringComparison.Ordinal))
            .ToArray();
}
