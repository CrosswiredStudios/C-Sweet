using CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.Projects;

public partial class ProjectReleases
{
    private static bool IsHistorical(WorkDeliveryExecutionResponse execution) => execution.Status is "Completed" or "Cancelled" or "Superseded";
    private static bool NeedsRecovery(WorkDeliveryExecutionResponse execution) => execution.Status is "Blocked" or "PartiallyPromoted";
    private static IEnumerable<WorkDeliveryExecutionResponse> CurrentExecutions(WorkDeliveryPlanResponse plan) =>
        plan.Executions.Where(x => !IsHistorical(x)).OrderByDescending(NeedsRecovery)
            .ThenByDescending(x => x.Status is "WaitingForApproval" or "WaitingForHuman");

    private static IEnumerable<string> CurrentBlockers(WorkDeliveryPlanResponse plan) =>
        plan.Status is "Completed" or "Cancelled" ? [] :
        plan.Findings.Where(x => x.Status == "Open").Select(x => x.Summary)
            .Concat(CurrentExecutions(plan).Where(NeedsRecovery).Select(x => x.BlockedReason ?? "A delivery check is blocked; inspect its evidence before recovery."))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    private static int AcceptedEpics(WorkDeliveryPlanResponse plan) => plan.Executions
        .Where(x => x.Scope == "Epic" && x.Status == "Completed" && x.ItemId.HasValue && plan.EpicItemIds.Contains(x.ItemId.Value))
        .Select(x => x.ItemId).Distinct().Count();

    private static string Readiness(WorkDeliveryPlanResponse plan)
    {
        if (plan.Status == "Completed") return "Delivered";
        if (plan.Status == "Cancelled") return "Cancelled";
        if (CurrentExecutions(plan).Any(x => x.Status == "PartiallyPromoted")) return "Partially integrated";
        if (CurrentBlockers(plan).Any()) return "Blocked";
        if (plan.Status == "Draft") return "Scope awaiting activation";
        if (plan.Status == "Paused") return "Paused";
        if (CurrentExecutions(plan).Any(x => x.Status == "WaitingForApproval")) return "Awaiting acceptance";
        if (CurrentExecutions(plan).Any(x => x.Status == "WaitingForHuman")) return "Awaiting review";
        if (CurrentExecutions(plan).Any(x => x.Status == "WaitingForEvidence")) return "Waiting for validation";
        if (CurrentExecutions(plan).Any() && CurrentExecutions(plan).All(x => x.Status == "WaitingForChildren")) return "Waiting for scoped work";
        if (!CurrentExecutions(plan).Any()) return "Not yet validated";
        return "Checks in progress";
    }

    private string PlanNextAction(WorkDeliveryPlanResponse plan)
    {
        var manager = PersonName(plan.ManagerOrganizationUserId);
        return plan.Status switch
        {
            "Completed" => "Delivery is complete. Additional scope needs a follow-up release.",
            "Cancelled" => "No further delivery is scheduled for this plan.",
            "Draft" => $"{manager}: review scope and staffing, then activate the approved scope.",
            "Paused" when CurrentBlockers(plan).Any() => $"{manager}: resolve the blockers and recover affected checks, then resume delivery.",
            "Paused" => $"{manager}: review the remaining checks, then resume delivery when appropriate.",
            _ when CurrentBlockers(plan).Any() => $"{manager}: resolve the blockers and recover affected checks.",
            _ when CurrentExecutions(plan).Any(x => x.Status is "WaitingForApproval" or "WaitingForHuman") => "Assigned reviewers must complete the pending reviews below.",
            _ => "Follow the remaining delivery checks below; acceptance is still required."
        };
    }

    private string ExecutionOwner(WorkDeliveryPlanResponse plan, WorkDeliveryExecutionResponse execution)
    {
        if (NeedsRecovery(execution) || execution.Status == "WaitingForChildren") return PersonName(plan.ManagerOrganizationUserId);
        if (execution.Status == "Promoting") return "Platform";
        var stage = execution.Stages.LastOrDefault(x => x.StageKey == execution.CurrentStageKey);
        var owner = stage?.OrganizationUserId ?? plan.Scopes.FirstOrDefault(x => x.Scope == execution.Scope && x.ItemId == execution.ItemId)?
            .Stages.FirstOrDefault(x => x.StageKey == execution.CurrentStageKey)?.OrganizationUserId;
        return owner.HasValue ? PersonName(owner) : stage?.PrincipalKind == "Platform" ? "Platform" : "Unassigned";
    }

    private static string ExecutionStatus(string status) => status switch
    {
        "PartiallyPromoted" => "Partially integrated",
        "WaitingForApproval" => "Awaiting acceptance",
        "WaitingForHuman" => "Awaiting review",
        "WaitingForChildren" => "Waiting for scoped work",
        "WaitingForEvidence" => "Waiting for validation",
        "Promoting" => "Integrating",
        _ => status
    };

    private static string StageName(string key) => key switch
    {
        "build-readiness" => "Build and validation",
        "quality" => "QA and regression testing",
        "technical-review" => "Technical review",
        "manager-review" => "Manager acceptance",
        "" => "Preparing delivery",
        _ => key.Replace('-', ' ')
    };

    private static string ExecutionNextAction(WorkDeliveryExecutionResponse execution) => execution.Status switch
    {
        "Blocked" => "Review the blocker, arrange any required QA-verified remediation, and recover this check.",
        "PartiallyPromoted" => "Inspect repository receipts and recover only the unfinished integrations.",
        "WaitingForApproval" => "Accept the exact candidate or request remediation against the acceptance criteria.",
        "WaitingForHuman" => "Review the exact candidate and record the required evidence and decision.",
        "WaitingForChildren" => "Complete and accept the required work in this scope before aggregate checks can proceed.",
        "WaitingForEvidence" => "Wait for build or test results; inspect validation evidence if progress stalls.",
        "Promoting" => "The platform must finish the approved repository integrations.",
        _ => $"The assigned owner must complete {StageName(execution.CurrentStageKey).ToLowerInvariant()}."
    };

    private static string BlockerSummary(string reason)
    {
        if (reason.StartsWith("Compute validation failed or its result is unknown", StringComparison.OrdinalIgnoreCase))
            return "Compute validation has failed or has no confirmed result.";
        return reason.Length <= 200 ? reason : reason[..197].TrimEnd() + "…";
    }
}
