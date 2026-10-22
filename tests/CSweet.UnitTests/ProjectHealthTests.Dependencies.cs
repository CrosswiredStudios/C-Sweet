using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Fact]
    public async Task SavedStageReportsReplaceUnsupportedContinuationClaimsWithCurrentDependencyFacts()
    {
        await using var f = await Fixture.Create();
        var (incident, ticket) = await PrepareOwnerReport(f, deliver: false);
        var sprint = new WorkSprint { Id = Guid.NewGuid(), BoardId = f.Board.Id, OrganizationId = f.Org, Status = WorkSprintStatus.Active };
        ticket.Status = WorkTaskStatus.Blocked; ticket.SprintId = sprint.Id;
        var child = f.Ticket(WorkTaskStatus.Ready); child.SprintId = sprint.Id;
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, Status = WorkItemExecutionStatus.Blocked };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id, StageKey = "quality", Status = WorkStageExecutionStatus.Blocked };
        f.Db.AddRange(sprint, child, execution, stage,
            new WorkItemDependency { WorkItemId = child.Id, DependsOnWorkItemId = ticket.Id },
            new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = 1,
                ResultJson = JsonSerializer.Serialize(new { diagnostics = new[] { "decision-required:v1" }, output = new {
                    criteria = new[] { new { unverifiable = true, missingEvidence = "Firefox and Safari are unavailable.", evidence = "Build passed." } } } }) });
        incident.Fingerprint = "stage:" + stage.Id.ToString("N");
        await f.Db.SaveChangesAsync();
        var state = await f.Service.CurrentIncidentStateAsync(incident, default);
        var report = ProjectHealthService.OwnerReport(incident)! with { SourceHash = state.Hash, Summary = "Art, audio and design work all continue." };
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id,
            new(incident.Id, incident.Revision, "dependency-report", OwnerReport: report), default);
        var saved = ProjectHealthService.OwnerReport(incident)!;
        Assert.DoesNotContain("all continue", saved.Summary);
        Assert.Contains("Firefox and Safari are unavailable", saved.Summary);
        Assert.Contains("1 waiting for prerequisites", saved.Summary);
        ManagerIncidentCoordinator.ValidateReport(saved);
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.Contains("Firefox and Safari are unavailable", presentation.Summary);
        Assert.Contains("none of the Ready tasks has completed prerequisites", presentation.Summary);
    }

    [Fact]
    public async Task CurrentDependencyFactsExcludeManagementWorkAndTrackTransitiveWaits()
    {
        await using var f = await Fixture.Create();
        var sprint = new WorkSprint { Id = Guid.NewGuid(), BoardId = f.Board.Id, OrganizationId = f.Org, Status = WorkSprintStatus.Active };
        var root = f.Ticket(WorkTaskStatus.Blocked); root.Identifier = "GAME-11"; root.SprintId = sprint.Id;
        var middle = f.Ticket(WorkTaskStatus.Ready); middle.SprintId = sprint.Id;
        var child = f.Ticket(WorkTaskStatus.Ready); child.SprintId = sprint.Id;
        var management = f.Ticket(WorkTaskStatus.Running); management.IsExecutable = false;
        f.Db.AddRange(sprint, root, middle, child, management,
            new WorkItemDependency { WorkItemId = middle.Id, DependsOnWorkItemId = root.Id },
            new WorkItemDependency { WorkItemId = child.Id, DependsOnWorkItemId = middle.Id });
        await f.Db.SaveChangesAsync();
        var incident = new ProjectIncident { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            AffectedWorkItemId = root.Id, Fingerprint = "ticket:" + root.Id.ToString("N"), Reason = "QA stopped" };
        var first = await f.Service.CurrentIncidentStateAsync(incident, default);
        var fact = Assert.Single(first.Facts, x => x.Kind == "CurrentDeliveryDependencyStatus");
        Assert.Contains("0 running; 0 Ready", fact.Detail);
        Assert.Contains("2 waiting for prerequisites", fact.Detail);
        Assert.Contains("2 task(s) depend directly or indirectly", fact.Detail);
        root.Status = WorkTaskStatus.Completed; root.Revision++;
        await f.Db.SaveChangesAsync();
        var changed = await f.Service.CurrentIncidentStateAsync(incident, default);
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.Contains("1 Ready with completed prerequisites; 1 waiting", Assert.Single(changed.Facts, x => x.Kind == "CurrentDeliveryDependencyStatus").Detail);
    }

    [Fact]
    public void AuthoritativeOwnerSummaryKeepsDependencyCountsWithinTheManagerWordBudget()
    {
        var dependencies = "Active sprint tasks: 0 running; 0 Ready with completed prerequisites; 5 waiting for prerequisites; 1 blocked.";
        var summary = ProjectHealthService.DependencyOwnerSummary(string.Concat(Enumerable.Repeat("missing evidence ", 100)), dependencies);
        Assert.Equal(80, summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith(dependencies, summary);
    }

    [Fact]
    public void RetainedQaOutputShowsMissingBrowsersInsteadOfSuccessfulEvidencePrefix()
    {
        var json = JsonSerializer.Serialize(new { diagnostics = new[] { "decision-required:v1" }, output = new {
            Criteria = new[] { new { Unverifiable = true, Evidence = string.Concat(Enumerable.Repeat("Build passed. ", 100)) +
                "Firefox and Safari cannot be executed; Chromium only is available." } } } });
        var text = ProjectHealthService.DecisionBlockerText(json);
        Assert.Contains("Firefox and Safari cannot be executed", text);
        Assert.DoesNotContain("Build passed", text);
        Assert.Null(ProjectHealthService.DecisionBlockerText("{}"));
        Assert.Null(ProjectHealthService.DecisionBlockerText("invalid JSON"));
    }
}
