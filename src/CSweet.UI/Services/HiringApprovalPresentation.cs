using CSweet.Contracts.Core;

namespace CSweet.UI.Services;

public sealed record RequestedHire(ResourceChangeRole Role, int Headcount);

public static class HiringApprovalPresentation
{
    public static string Title(ApprovalDashboardItemResponse approval)
    {
        if (approval.ResourceChange is { } request)
        {
            var team = string.IsNullOrWhiteSpace(approval.RequestingTeam) ? request.TeamName : approval.RequestingTeam;
            return string.IsNullOrWhiteSpace(team) ? "Hiring plan" : $"{team} hiring plan";
        }
        return approval.HiringWorkflow is { } workflow ? $"Hire {workflow.EmployeeDisplayName}" : approval.Title;
    }

    // Deltas describe desired totals. Only the additional seats require new hires.
    public static IReadOnlyList<RequestedHire> RequestedHires(ResourceChangeRequestResponse request) => request.Deltas
        .Where(delta => delta.ChangeKind is "Add" or "Increase" or "Modify")
        .Select(delta => new RequestedHire(delta.Role,
            Math.Max(0, delta.Role.Headcount - (delta.PreviousRole?.Headcount ?? 0))))
        .Where(hire => hire.Headcount > 0)
        .OrderBy(hire => hire.Role.Priority)
        .ToList();
}
