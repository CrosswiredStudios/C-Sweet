namespace CSweet.Domain.Core;

public sealed class CompanyDashboardReport
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Kind { get; set; } = "";
    public Guid? WorkstreamId { get; set; }
    public Guid ReporterOrganizationUserId { get; set; }
    public string ReporterName { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
public sealed class CompanyDashboardLayout
{
    public Guid OrganizationId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public string OrderJson { get; set; } = "[]";
}
