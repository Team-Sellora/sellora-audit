using Sellora.AuditService.Domain.Tenancy;

namespace Sellora.AuditService.Domain.Reporting;

/// <summary>
/// Known reps, territories and agencies, so a breakdown can list a member
/// with zero sales. Values only fill in or move to a newer non-empty value.
/// </summary>
public sealed class RepDimension : ITenantScoped
{
    private RepDimension()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid SalesRepId { get; private set; }

    public string? Name { get; private set; }

    public Guid? TerritoryId { get; private set; }

    public Guid? AgencyId { get; private set; }

    public Guid? ProvinceId { get; private set; }

    public static RepDimension Start(Guid companyId, Guid salesRepId) => new() { CompanyId = companyId, SalesRepId = salesRepId };

    public void Update(string? name, Guid? territoryId, Guid? agencyId, Guid? provinceId)
    {
        Name = string.IsNullOrWhiteSpace(name) ? Name : name;
        TerritoryId = territoryId ?? TerritoryId;
        AgencyId = agencyId ?? AgencyId;
        ProvinceId = provinceId ?? ProvinceId;
    }
}

public sealed class TerritoryDimension : ITenantScoped
{
    private TerritoryDimension()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid TerritoryId { get; private set; }

    // Hierarchy events do not name territories; the name comes from Organization.
    public string? Name { get; private set; }

    public Guid? AgencyId { get; private set; }

    public Guid? ProvinceId { get; private set; }

    public static TerritoryDimension Start(Guid companyId, Guid territoryId) => new() { CompanyId = companyId, TerritoryId = territoryId };

    public void Update(Guid? agencyId, Guid? provinceId)
    {
        AgencyId = agencyId ?? AgencyId;
        ProvinceId = provinceId ?? ProvinceId;
    }

    public void Rename(string? name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? Name : name;
    }
}

public sealed class AgencyDimension : ITenantScoped
{
    private AgencyDimension()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid AgencyId { get; private set; }

    public string? Name { get; private set; }

    public Guid? ProvinceId { get; private set; }

    public static AgencyDimension Start(Guid companyId, Guid agencyId) => new() { CompanyId = companyId, AgencyId = agencyId };

    public void Update(string? name, Guid? provinceId)
    {
        Name = string.IsNullOrWhiteSpace(name) ? Name : name;
        ProvinceId = provinceId ?? ProvinceId;
    }
}
