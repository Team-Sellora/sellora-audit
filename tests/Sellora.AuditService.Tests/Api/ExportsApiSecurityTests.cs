using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sellora.AuditService.Application.Exports;
using Sellora.AuditService.Application.Identity;
using Sellora.AuditService.Tests.Support;

namespace Sellora.AuditService.Tests.Api;

/// <summary>US-E7-3-BE1: who may call the export routes; the store is a recording stub, no database.</summary>
public sealed class ExportsApiSecurityTests : IClassFixture<ExportsApiSecurityTests.ExportsApi>
{
    private const string Export = "/api/reports/export";
    private static readonly Guid Company = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private readonly ExportsApi _api;

    public ExportsApiSecurityTests(ExportsApi api)
    {
        _api = api;
        _api.Organization.Unavailable = false;
    }

    private static object Summary(string type = "sales-summary", string format = "csv") =>
        new { reportType = type, format, filters = new { from = "2026-09-01", to = "2026-09-30", groupBy = "rep" } };

    [Theory]
    [InlineData("POST", Export)]
    [InlineData("GET", Export)]
    [InlineData("GET", "/api/reports/export/7d4c0b5e-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/reports/export/7d4c0b5e-0000-0000-0000-000000000001/file")]
    public async Task An_anonymous_caller_is_unauthorised(string verb, string path)
    {
        using var client = _api.CreateClientAs(roles: null);

        var response = await client.SendAsync(Request(verb, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("SalesRep", "POST", Export)]
    [InlineData("AgencyOperator", "POST", Export)]
    [InlineData("ShopOwner", "GET", Export)]
    [InlineData("SalesRep", "GET", "/api/reports/export/7d4c0b5e-0000-0000-0000-000000000001/file")]
    public async Task Other_roles_are_forbidden(string role, string verb, string path)
    {
        using var client = _api.CreateClientAs(role, Company);

        var response = await client.SendAsync(Request(verb, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_a_company_is_unauthorised()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", companyId: null);

        var response = await client.PostAsJsonAsync(Export, Summary());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_area_manager_cannot_export_an_audit_extract()
    {
        _api.Organization.Scope = new CallerScope(null, null, null, new[] { Guid.NewGuid() });
        using var client = _api.CreateClientAs("AreaManager", Company, subject: $"am-{Guid.NewGuid():N}");

        var response = await client.PostAsJsonAsync(Export, new { reportType = "audit-extract", format = "csv" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public static TheoryData<string> InvalidBodies => new()
    {
        """{ "reportType": "payroll", "format": "csv", "filters": { "from": "2026-09-01", "to": "2026-09-30" } }""",
        """{ "reportType": "sales-summary", "format": "xlsx", "filters": { "from": "2026-09-01", "to": "2026-09-30" } }""",
        """{ "reportType": "sales-summary", "format": "csv", "filters": { "from": "2026-10-02", "to": "2026-10-01" } }""",
        """{ "reportType": "sales-summary", "format": "csv", "filters": { "from": "2024-01-01", "to": "2027-01-05" } }""",
        """{ "reportType": "sales-summary", "format": "csv", "filters": { "from": "2026-09-31", "to": "2026-10-01" } }""",
        """{ "reportType": "sales-breakdown", "format": "csv", "filters": { "from": "2026-09-01", "to": "2026-09-30" } }""",
        """{ "reportType": "audit-extract", "format": "csv", "filters": { "occurredFrom": "2026-10-02T00:00:00Z", "occurredTo": "2026-10-01T00:00:00Z" } }"""
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_is_a_bad_request(string body)
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company);

        var response = await client.PostAsync(Export, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_submission_is_accepted_at_once_with_an_export_id()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company, subject: "admin-1");

        var response = await client.PostAsJsonAsync(Export, Summary("sales-breakdown", "pdf"));
        var accepted = await response.Content.ReadFromJsonAsync<ExportAccepted>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotEqual(Guid.Empty, accepted!.ExportId);
        Assert.EndsWith($"/api/reports/export/{accepted.ExportId}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(_api.Exports.LastRequester!.ProvinceIds);
    }

    [Fact]
    public async Task The_scope_comes_from_the_token_never_from_the_body()
    {
        var province = Guid.NewGuid();
        var subject = $"am-{Guid.NewGuid():N}";
        _api.Organization.Scope = new CallerScope(null, null, null, new[] { province });
        using var client = _api.CreateClientAs("AreaManager", Company, subject);

        var response = await client.PostAsJsonAsync(Export, new
        {
            reportType = "sales-summary",
            format = "csv",
            provinceIds = new[] { Guid.NewGuid() },
            companyId = Guid.NewGuid(),
            requesterSub = "someone-else",
            filters = new { from = "2026-09-01", to = "2026-09-30", provinceIds = new[] { Guid.NewGuid() } }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var requester = _api.Exports.LastRequester!;
        Assert.Equal(new[] { province }, requester.ProvinceIds);
        Assert.Equal(Company, requester.CompanyId);
        Assert.Equal(subject, requester.Subject);
        Assert.Equal("AreaManager", requester.Role);
    }

    [Fact]
    public async Task Organization_being_down_is_a_503_for_an_area_manager()
    {
        _api.Organization.Unavailable = true;
        using var client = _api.CreateClientAs("AreaManager", Company, subject: $"am-{Guid.NewGuid():N}");

        var response = await client.PostAsJsonAsync(Export, Summary());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Another_user_gets_404_for_the_status_and_the_file()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company, subject: "someone-else");

        var status = await client.GetAsync($"{Export}/{RecordingExports.ReadyId}");
        var file = await client.GetAsync($"{Export}/{RecordingExports.ReadyId}/file");

        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }

    [Fact]
    public async Task The_owner_gets_410_after_expiry_and_409_before_it_is_ready()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company, subject: RecordingExports.Owner);

        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"{Export}/{RecordingExports.ExpiredId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"{Export}/{RecordingExports.QueuedId}/file")).StatusCode);
    }

    [Fact]
    public async Task The_owner_downloads_with_a_safe_file_name_and_no_caching()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company, subject: RecordingExports.Owner);

        var response = await client.GetAsync($"{Export}/{RecordingExports.ReadyId}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("evilreport.csv", response.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(RecordingExports.Bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Cors_exposes_content_disposition_to_the_web_app()
    {
        using var client = _api.CreateClientAs("CompanyAdmin", Company, subject: RecordingExports.Owner);
        var request = Request("GET", $"{Export}/{RecordingExports.ReadyId}/file");
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        Assert.Contains("Content-Disposition", response.Headers.GetValues("Access-Control-Expose-Headers"));
    }

    private static HttpRequestMessage Request(string verb, string path) =>
        verb == "POST"
            ? new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(Summary()) }
            : new HttpRequestMessage(new HttpMethod(verb), path);

    public sealed class ExportsApi : AuditApiFactory
    {
        public RecordingExports Exports { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IExportService>(Exports));
        }
    }

    /// <summary>Applies the real request rules and records who asked; serves canned files to one owner.</summary>
    public sealed class RecordingExports : IExportService
    {
        public const string Owner = "owner-sub";
        public static readonly Guid ReadyId = Guid.Parse("7d4c0b5e-0000-0000-0000-000000000001");
        public static readonly Guid ExpiredId = Guid.Parse("7d4c0b5e-0000-0000-0000-000000000002");
        public static readonly Guid QueuedId = Guid.Parse("7d4c0b5e-0000-0000-0000-000000000003");
        public static readonly byte[] Bytes = { 0xEF, 0xBB, 0xBF, (byte)'a' };

        public ExportRequester? LastRequester { get; private set; }

        public Task<ExportSubmitResult> SubmitAsync(ExportRequester requester, CreateExportRequest request, CancellationToken cancellationToken)
        {
            var check = ExportRequests.Check(requester.Role, request);

            if (check.Status != ExportSubmitStatus.Accepted)
            {
                return Task.FromResult(new ExportSubmitResult(check.Status, null, check.Message));
            }

            LastRequester = requester;
            return Task.FromResult(new ExportSubmitResult(ExportSubmitStatus.Accepted, Guid.NewGuid(), null));
        }

        public Task<ExportStatusResponse?> GetStatusAsync(Guid companyId, string subject, Guid exportId, CancellationToken cancellationToken) =>
            Task.FromResult(subject == Owner && exportId == ReadyId
                ? new ExportStatusResponse(exportId, "sales-summary", "csv", "Ready", DateTimeOffset.UtcNow, null, null, null, "a.csv",
                    ExportStatusResponse.FilePath(exportId), null)
                : null);

        public Task<ExportFileResult> GetFileAsync(Guid companyId, string subject, Guid exportId, CancellationToken cancellationToken) =>
            Task.FromResult(subject != Owner
                ? new ExportFileResult(ExportFileStatus.NotFound)
                : exportId == ReadyId
                    ? new ExportFileResult(ExportFileStatus.Ready, "../evil\"report.csv", "text/csv; charset=utf-8", Bytes)
                    : exportId == ExpiredId
                        ? new ExportFileResult(ExportFileStatus.Gone)
                        : exportId == QueuedId
                            ? new ExportFileResult(ExportFileStatus.NotReady)
                            : new ExportFileResult(ExportFileStatus.NotFound));

        public Task<IReadOnlyList<ExportStatusResponse>> ListMineAsync(Guid companyId, string subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExportStatusResponse>>(Array.Empty<ExportStatusResponse>());
    }
}
