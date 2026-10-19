using CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.Projects;

public partial class ProjectReleases
{
    private static bool IsHistorical(WorkDeliveryExecutionResponse execution) => execution.Status is "Completed" or "Cancelled" or "Superseded";
    private static bool NeedsRecovery(WorkDeliveryExecutionResponse execution) => execution.Status is "Blocked" or "PartiallyPromoted";
    private static IEnumerable<WorkDeliveryExecutionResponse> CurrentExecutions(WorkDeliveryPlanResponse plan) =>
        plan.Executions.Where(x => !IsHistorical(x)).OrderByDescending(NeedsRecovery)
            .ThenByDescending(x => x.Status is "WaitingForApproval" or "WaitingForHuman");

    private IEnumerable<string> CurrentBlockers(WorkDeliveryPlanResponse plan) =>
        plan.Status is "Completed" or "Cancelled" ? [] :
        plan.Findings.Where(x => x.Status == "Open").Select(x => x.Summary)
            .Concat(CurrentExecutions(plan).Where(NeedsRecovery).Select(x => x.BlockedReason ?? L["ProjectReleases.Blocker.Default"]))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    private static int AcceptedEpics(WorkDeliveryPlanResponse plan) => plan.Executions
        .Where(x => x.Scope == "Epic" && x.Status == "Completed" && x.ItemId.HasValue && plan.EpicItemIds.Contains(x.ItemId.Value))
        .Select(x => x.ItemId).Distinct().Count();

    private string Readiness(WorkDeliveryPlanResponse plan)
    {
        if (plan.Status == "Completed") return L["ProjectReleases.Readiness.Delivered"];
        if (plan.Status == "Cancelled") return L["ProjectReleases.Readiness.Cancelled"];
        if (CurrentExecutions(plan).Any(x => x.Status == "PartiallyPromoted")) return L["ProjectReleases.Readiness.PartiallyIntegrated"];
        if (CurrentBlockers(plan).Any()) return L["ProjectReleases.Readiness.Blocked"];
        if (plan.Status == "Draft") return L["ProjectReleases.Readiness.Draft"];
        if (plan.Status == "Paused") return L["ProjectReleases.Readiness.Paused"];
        if (CurrentExecutions(plan).Any(x => x.Status == "WaitingForApproval")) return L["ProjectReleases.Readiness.AwaitingAcceptance"];
        if (CurrentExecutions(plan).Any(x => x.Status == "WaitingForHuman")) return L["ProjectReleases.Readiness.AwaitingReview"];
        if (CurrentExecutions(plan).Any(x => x.Status == "WaitingForEvidence")) return L["ProjectReleases.Readiness.WaitingForValidation"];
        if (CurrentExecutions(plan).Any() && CurrentExecutions(plan).All(x => x.Status == "WaitingForChildren")) return L["ProjectReleases.Readiness.WaitingForScopedWork"];
        if (!CurrentExecutions(plan).Any()) return L["ProjectReleases.Readiness.NotYetValidated"];
        return L["ProjectReleases.Readiness.ChecksInProgress"];
    }

    private string PlanNextAction(WorkDeliveryPlanResponse plan)
    {
        var manager = PersonName(plan.ManagerOrganizationUserId);
        return plan.Status switch
        {
            "Completed" => L["ProjectReleases.PlanNext.Completed"],
            "Cancelled" => L["ProjectReleases.PlanNext.Cancelled"],
            "Draft" => L.Format("ProjectReleases.PlanNext.Draft", new { manager }),
            "Paused" when CurrentBlockers(plan).Any() => L.Format("ProjectReleases.PlanNext.PausedBlocked", new { manager }),
            "Paused" => L.Format("ProjectReleases.PlanNext.Paused", new { manager }),
            _ when CurrentBlockers(plan).Any() => L.Format("ProjectReleases.PlanNext.Blocked", new { manager }),
            _ when CurrentExecutions(plan).Any(x => x.Status is "WaitingForApproval" or "WaitingForHuman") => L["ProjectReleases.PlanNext.Reviews"],
            _ => L["ProjectReleases.PlanNext.Default"]
        };
    }

    private string ExecutionOwner(WorkDeliveryPlanResponse plan, WorkDeliveryExecutionResponse execution)
    {
        if (NeedsRecovery(execution) || execution.Status == "WaitingForChildren") return PersonName(plan.ManagerOrganizationUserId);
        if (execution.Status == "Promoting") return L["ProjectReleases.Owner.Platform"];
        var stage = execution.Stages.LastOrDefault(x => x.StageKey == execution.CurrentStageKey);
        var owner = stage?.OrganizationUserId ?? plan.Scopes.FirstOrDefault(x => x.Scope == execution.Scope && x.ItemId == execution.ItemId)?
            .Stages.FirstOrDefault(x => x.StageKey == execution.CurrentStageKey)?.OrganizationUserId;
        return owner.HasValue ? PersonName(owner) : stage?.PrincipalKind == "Platform" ? L["ProjectReleases.Owner.Platform"] : L["ProjectReleases.Owner.Unassigned"];
    }

    private string ExecutionStatus(string status) => status switch
    {
        "PartiallyPromoted" or "WaitingForApproval" or "WaitingForHuman" or "WaitingForChildren" or "WaitingForEvidence" or "Promoting" => L["ProjectReleases.Status." + status],
        _ => status
    };

    private string StageName(string key) => key switch
    {
        "build-readiness" => L["ProjectReleases.Stage.BuildReadiness"],
        "quality" => L["ProjectReleases.Stage.Quality"],
        "technical-review" => L["ProjectReleases.Stage.TechnicalReview"],
        "manager-review" => L["ProjectReleases.Stage.ManagerReview"],
        "" => L["ProjectReleases.Stage.Preparing"],
        _ => key.Replace('-', ' ')
    };

    private string ExecutionNextAction(WorkDeliveryExecutionResponse execution) => execution.Status switch
    {
        "Blocked" or "PartiallyPromoted" or "WaitingForApproval" or "WaitingForHuman" or "WaitingForChildren" or "WaitingForEvidence" or "Promoting" => L["ProjectReleases.Next." + execution.Status],
        _ => L.Format("ProjectReleases.Next.Default", new { stage = StageName(execution.CurrentStageKey).ToLowerInvariant() })
    };

    private string BlockerSummary(string reason)
    {
        if (reason.StartsWith("Compute validation failed or its result is unknown", StringComparison.OrdinalIgnoreCase))
            return L["ProjectReleases.Blocker.ComputeValidation"];
        return reason.Length <= 200 ? reason : reason[..197].TrimEnd() + "…";
    }
}
