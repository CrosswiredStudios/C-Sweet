using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class CompanyDashboardService(CSweetDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CompanyDashboardResponse> ReadAsync(Guid organizationId, CancellationToken token)
    {
        var reporters = await EligibleAsync(organizationId, token);
        return new(await WidgetAsync<FinanceReport>("finance", CompanyReportingCapabilities.Finance),
            await WidgetAsync<LegalReport>("legal", CompanyReportingCapabilities.Legal));
        async Task<ReportingWidget<T>> WidgetAsync<T>(string kind, string capability)
        {
            var record = await db.Set<CompanyDashboardReport>().AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Kind == kind)
                .OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
            var report = record is null ? null : new DashboardReport<T>(
                JsonSerializer.Deserialize<T>(record.PayloadJson, Json)!, record.ReporterOrganizationUserId,
                record.ReporterName, record.PublishedAt);
            return new(reporters.Where(x => x.Capabilities.Contains(capability, StringComparer.Ordinal))
                .Select(x => new DashboardReporter(x.Id, x.Name)).ToArray(), report);
        }
    }

    public async Task<IReadOnlyList<string>> LayoutAsync(Guid organizationId, Guid actorId, CancellationToken token)
    {
        var record = await db.Set<CompanyDashboardLayout>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.OrganizationUserId == actorId, token);
        if (record is null) return DashboardWidgets.DefaultOrder;
        try
        {
            var order = JsonSerializer.Deserialize<string[]>(record.OrderJson);
            return DashboardWidgets.Restore(order);
        }
        catch (JsonException) { return DashboardWidgets.DefaultOrder; }
    }

    public async Task SaveLayoutAsync(Guid organizationId, Guid actorId, DashboardLayoutRequest request, CancellationToken token)
    {
        if (!DashboardWidgets.IsValid(request.Order)) throw new ArgumentException("Include each dashboard widget exactly once.");
        var record = await db.Set<CompanyDashboardLayout>().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.OrganizationUserId == actorId, token);
        if (record is null)
        {
            record = new() { OrganizationId = organizationId, OrganizationUserId = actorId };
            db.Add(record);
        }
        record.OrderJson = JsonSerializer.Serialize(request.Order);
        await db.SaveChangesAsync(token);
    }

    public async Task PublishAsync(Guid organizationId, Guid installationId, string capability, JsonElement payload, CancellationToken token)
    {
        var reporters = await EligibleAsync(organizationId, token);
        var actor = reporters.SingleOrDefault(x => x.InstallationId == installationId &&
            x.Capabilities.Contains(capability, StringComparer.Ordinal))
            ?? throw new UnauthorizedAccessException("An active employee with this reporting grant is required.");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        string kind;
        Guid? workstreamId = null;
        object data;
        switch (capability)
        {
            case CompanyReportingCapabilities.Finance:
                var finance = payload.Deserialize<FinanceReport>(Json) ?? throw new ArgumentException("A finance report is required.");
                if (finance.AsOf == default || finance.AsOf > today || finance.Currency is null ||
                    finance.Currency.Length != 3 || !finance.Currency.All(c => c is >= 'A' and <= 'Z') ||
                    (finance.Revenue is null && finance.Expenses is null && finance.CashBalance is null && finance.MonthlyBudget is null) ||
                    finance.MonthlyBudget < 0)
                    throw new ArgumentException("Supply an as-of date, uppercase three-letter currency, and at least one metric; budget cannot be negative.");
                kind = "finance"; data = finance; break;
            case CompanyReportingCapabilities.Legal:
                var legal = payload.Deserialize<LegalReport>(Json) ?? throw new ArgumentException("A legal report is required.");
                if (!Text(legal.EntityName, 300) || !Text(legal.EntityType, 100) || !Text(legal.Status, 100) ||
                    legal.VerifiedOn == default || legal.VerifiedOn > today || legal.Obligations is null ||
                    legal.Obligations.Count > 100 || legal.Obligations.Any(x => x is null || !Text(x.Title, 300) || x.DueDate == default))
                    throw new ArgumentException("Supply entity details, verification date, and dated obligations.");
                kind = "legal"; data = legal; break;
            case CompanyReportingCapabilities.Project:
                var project = payload.Deserialize<ProjectLeadUpdate>(Json) ?? throw new ArgumentException("A project update is required.");
                if (!Text(project.Summary, 2000)) throw new ArgumentException("The update must contain 1–2,000 characters.");
                if (!await db.Workstreams.AnyAsync(x => x.OrganizationId == organizationId && x.Id == project.WorkstreamId &&
                    x.AccountableManagerOrganizationUserId == actor.Id, token))
                    throw new UnauthorizedAccessException("Only the accountable project lead can publish this update.");
                kind = "project"; workstreamId = project.WorkstreamId; data = project; break;
            default: throw new ArgumentException("Unknown reporting capability.");
        }
        db.Add(new CompanyDashboardReport
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Kind = kind, WorkstreamId = workstreamId,
            ReporterOrganizationUserId = actor.Id, ReporterName = actor.Name,
            PublishedAt = clock.GetUtcNow(), PayloadJson = JsonSerializer.Serialize(data, Json)
        });
        db.ApplicationRealtimeOutbox.Add(new CSweet.Domain.Notifications.ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, EventType = CompanyReportingCapabilities.ReportChanged,
            Subject = $"organizations/{organizationId}/dashboard", DataJson = "{}",
            OccurredAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(token);
    }

    private async Task<IReadOnlyList<EligibleReporter>> EligibleAsync(Guid organizationId, CancellationToken token)
    {
        var businessId = organizationId.ToString();
        var candidates = await (from employee in db.CoreOrganizationUsers.AsNoTracking()
            join installation in db.AgentInstallations.AsNoTracking() on employee.AgentInstallationId equals installation.Id
            join grant in db.AgentInstallationGrants.AsNoTracking() on installation.Id equals grant.AgentInstallationId
            where employee.OrganizationId == organizationId && employee.IsActive && installation.BusinessId == businessId &&
                installation.IsEnabled && installation.RevisionStatus == PluginRevisionStatus.Active && installation.SetupState == PluginSetupState.Ready
            orderby employee.DisplayName
            select new { employee.Id, employee.DisplayName, InstallationId = installation.Id, grant.RequiredCapabilitiesJson }).ToListAsync(token);
        return candidates.Select(x => new EligibleReporter(x.Id, x.DisplayName, x.InstallationId, ParseCapabilities(x.RequiredCapabilitiesJson))).ToArray();
    }
    private static string[] ParseCapabilities(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch (JsonException) { return []; }
    }
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private sealed record EligibleReporter(Guid Id, string Name, Guid InstallationId, string[] Capabilities);
}
