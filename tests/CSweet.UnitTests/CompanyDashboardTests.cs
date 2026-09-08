using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace CSweet.UnitTests;

public sealed class CompanyDashboardTests
{
    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static readonly FinanceReport Finance = new(new DateOnly(2026, 1, 5), "USD", 0m, null, 100m, null);
    private static JsonElement Payload<T>(T value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static async Task<(Guid Org, Guid Installation, Guid Employee)> Reporter(CSweetDbContext db, string capability)
    {
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var employee = Guid.NewGuid();
        db.AddRange(new Organization { Id = org, Name = "Example" },
            new AgentInstallation { Id = installation, BusinessId = org.ToString(), IsEnabled = true },
            new OrganizationUser { Id = employee, OrganizationId = org, AgentInstallationId = installation, DisplayName = "Independent reporting agent", IsActive = true },
            new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation, RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { capability }) });
        await db.SaveChangesAsync();
        return (org, installation, employee);
    }
    [Fact]
    public async Task OnboardingTransitionsFromNoReporterToAwaitingReportWithoutInventingMetrics()
    {
        await using var db = Database();
        var service = new CompanyDashboardService(db, TimeProvider.System);
        var empty = await service.ReadAsync(Guid.NewGuid(), default);
        Assert.Empty(empty.Finance.Reporters); Assert.Null(empty.Finance.Report);
        var ids = await Reporter(db, CompanyReportingCapabilities.Finance);
        var waiting = await service.ReadAsync(ids.Org, default);
        Assert.Single(waiting.Finance.Reporters); Assert.Null(waiting.Finance.Report);
        Assert.Empty(waiting.Legal.Reporters);
        await service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Finance, Payload(Finance), default);
        var published = (await service.ReadAsync(ids.Org, default)).Finance.Report!;
        Assert.Equal(0m, published.Data.Revenue); Assert.Null(published.Data.Expenses);
        Assert.Equal(ids.Employee, published.ReporterId);
    }
    [Fact]
    public async Task FormerReportSurvivesDisabledReporterAndOtherOrganizationsCannotSeeOrPublishIt()
    {
        await using var db = Database(); var ids = await Reporter(db, CompanyReportingCapabilities.Finance);
        var service = new CompanyDashboardService(db, TimeProvider.System);
        await service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Finance, Payload(Finance), default);
        db.AgentInstallations.Single().IsEnabled = false; await db.SaveChangesAsync();
        var result = await service.ReadAsync(ids.Org, default);
        Assert.Empty(result.Finance.Reporters); Assert.NotNull(result.Finance.Report);
        Assert.Null((await service.ReadAsync(Guid.NewGuid(), default)).Finance.Report);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Finance, Payload(Finance), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PublishAsync(Guid.NewGuid(), ids.Installation, CompanyReportingCapabilities.Finance, Payload(Finance), default));
    }
    [Fact]
    public async Task WrongGrantsAndInvalidReportsAreRejectedWithoutPersisting()
    {
        await using var db = Database(); var ids = await Reporter(db, CompanyReportingCapabilities.Finance);
        var service = new CompanyDashboardService(db, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Legal, Payload(new {}), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Finance, Payload(Finance with { Currency = "invalid" }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Finance, Payload(Finance with { Revenue = null, CashBalance = null }), default));
        Assert.Empty(db.Set<CompanyDashboardReport>());
    }
    [Fact]
    public async Task OnlyAccountableLeadCanPublishProjectUpdate()
    {
        await using var db = Database(); var ids = await Reporter(db, CompanyReportingCapabilities.Project);
        var service = new CompanyDashboardService(db, TimeProvider.System);
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = ids.Org, Name = "Portal", AccountableManagerOrganizationUserId = Guid.NewGuid() };
        db.Add(project); await db.SaveChangesAsync();
        var payload = Payload(new ProjectLeadUpdate(project.Id, "Beta is ready for QA."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Project, payload, default));
        project.AccountableManagerOrganizationUserId = ids.Employee; await db.SaveChangesAsync();
        await service.PublishAsync(ids.Org, ids.Installation, CompanyReportingCapabilities.Project, payload, default);
        Assert.Equal(project.Id, db.Set<CompanyDashboardReport>().Single().WorkstreamId);
    }
    [Fact]
    public async Task LayoutPersistsPerUserAndCompanyAndRejectsDuplicates()
    {
        await using var db = Database(); var service = new CompanyDashboardService(db, TimeProvider.System);
        var org = Guid.NewGuid(); var actor = Guid.NewGuid();
        var order = new[] { "projects", "legal", "finance", "approvals" };
        await service.SaveLayoutAsync(org, actor, new(order), default);
        Assert.Equal(order, await service.LayoutAsync(org, actor, default));
        Assert.Equal(DashboardWidgets.DefaultOrder, await service.LayoutAsync(org, Guid.NewGuid(), default));
        Assert.Equal(DashboardWidgets.DefaultOrder, await service.LayoutAsync(Guid.NewGuid(), actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveLayoutAsync(org, actor, new(["finance", "finance", "legal", "projects"]), default));
        Assert.Equal(order, await service.LayoutAsync(org, actor, default));
    }
    [Fact]
    public void ReportingToolsAreDiscoverableOnlyWithTheirGrantAndHaveBoundedSchemas()
    {
        var catalog = new McpToolCatalog([]);
        Assert.DoesNotContain(catalog.List(new HashSet<string>()), x => x.Capability == CompanyReportingCapabilities.Finance);
        var tools = catalog.List(new HashSet<string> { CompanyReportingCapabilities.Finance, CompanyReportingCapabilities.Legal, CompanyReportingCapabilities.Project });
        Assert.Equal(3, tools.Count);
        Assert.All(tools, tool => Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean()));
    }
}
