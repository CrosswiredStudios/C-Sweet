namespace CSweet.Contracts.Setup;

public sealed record HostedOfficePlan(int Linked, int Standard, int Team, bool CanManage);
public sealed record HostedOfficeOverview(HostedOfficePlan? Plan, string? PlanError, string ManageUrl, bool Reviewed);
