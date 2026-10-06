namespace CSweet.Contracts.Core;

public static class CompanyReportingCapabilities
{
    public const string ReportChanged = "com.csweet.company.report.changed.v1";
    public const string Finance = "platform.company.finance-report.v1";
    public const string Legal = "platform.company.legal-report.v1";
    public const string Project = "platform.company.project-update.v1";
}
public sealed record FinanceReport(DateOnly AsOf, string Currency, decimal? Revenue,
    decimal? Expenses, decimal? CashBalance, decimal? MonthlyBudget);
public sealed record LegalObligation(string Title, DateOnly DueDate);
public sealed record LegalReport(string EntityName, string EntityType, string Status,
    DateOnly VerifiedOn, IReadOnlyList<LegalObligation> Obligations);
public sealed record ProjectLeadUpdate(Guid WorkstreamId, string Summary);
public sealed record DashboardReporter(Guid Id, string Name);
public sealed record DashboardReport<T>(T Data, Guid ReporterId, string ReporterName, DateTimeOffset PublishedAt);
public sealed record ReportingWidget<T>(IReadOnlyList<DashboardReporter> Reporters, DashboardReport<T>? Report);
public sealed record CompanyDashboardResponse(ReportingWidget<FinanceReport> Finance, ReportingWidget<LegalReport> Legal);
public sealed record DashboardLayoutRequest(IReadOnlyList<string> Order);
public static class DashboardWidgets
{
    public static IReadOnlyList<string> DefaultOrder { get; } = Array.AsReadOnly(new[] { "decisions", "hiring", "approvals", "activity", "finance", "legal", "projects" });
    public static bool IsValid(IReadOnlyList<string>? order) => order is not null && order.Count == DefaultOrder.Count &&
        order.Distinct(StringComparer.Ordinal).Count() == DefaultOrder.Count && order.All(DefaultOrder.Contains);
    public static IReadOnlyList<string> Restore(IReadOnlyList<string>? order)
    {
        if (IsValid(order)) return order!;
        // Append newly introduced widgets without disturbing a user's saved arrangement.
        if (order is { Count: 4 or 5 or 6 } && !order.Contains("hiring") && order.Distinct(StringComparer.Ordinal).Count() == order.Count &&
            new[] { "approvals", "finance", "legal", "projects" }.All(order.Contains) && order.All(DefaultOrder.Contains))
            return order.Concat(new[] { "decisions", "activity", "hiring" }.Where(x => !order.Contains(x))).ToArray();
        return DefaultOrder;
    }
}
