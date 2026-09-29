using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class ProjectStageAssignmentTests
{
    [Theory]
    [InlineData("workspace-valid")]
    [InlineData("workspace-wrong-agent")]
    [InlineData("workspace-completed-stage")]
    [InlineData("workspace-cancelled")]
    [InlineData("workspace-missing-participant")]
    [InlineData("workspace-no-attempt")]
    [InlineData("valid")]
    [InlineData("valid-queued")]
    [InlineData("valid-dispatching")]
    [InlineData("wrong-dispatching-attempt")]
    [InlineData("wrong-queued-attempt")]
    [InlineData("wrong-agent")]
    [InlineData("wrong-employee")]
    [InlineData("wrong-organization")]
    [InlineData("wrong-item")]
    [InlineData("archived-ticket")]
    [InlineData("foreign-ticket")]
    [InlineData("wrong-board")]
    [InlineData("wrong-policy")]
    [InlineData("wrong-stage")]
    [InlineData("wrong-traversal")]
    [InlineData("wrong-attempt")]
    [InlineData("cancelled")]
    [InlineData("completed-stage")]
    [InlineData("missing-participant")]
    [InlineData("legacy-capability")]
    public async Task Dispatch_requires_the_exact_active_stage_owner_and_project_membership(string condition)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var workspace = condition.StartsWith("workspace-", StringComparison.Ordinal);
        var scenario = workspace ? condition[10..] : condition;
        var org = Guid.NewGuid();
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        var engineer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid() };
        db.AddRange(human, engineer);
        db.AgentInstallations.Add(new() { Id = engineer.AgentInstallationId!.Value, BusinessId = org.ToString(), IsEnabled = true,
            PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}" } });
        await db.SaveChangesAsync();
        var policy = new ProjectWorkPolicy(db, TimeProvider.System);
        var setup = new ProjectSetupService(db, TimeProvider.System, policy);
        var project = await setup.CreateAsync(human, new("Game", "Build the game", human.Id, null, [engineer.Id], null, null, null, "test"), default);
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = project.BoardId };
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org, BoardId = project.BoardId,
            SprintId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(), Status = WorkSprintExecutionStatus.Active };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, SprintExecutionId = sprint.Id,
            CurrentStageKey = "implementation", Status = WorkItemExecutionStatus.Pending };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = item.Id, StageKey = item.CurrentStageKey,
            StageType = WorkOrchestrationStageType.AgentExecution, Status = WorkStageExecutionStatus.Pending,
            PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation, OrganizationUserId = engineer.Id,
            AgentInstallationId = engineer.AgentInstallationId };
        item.Stages.Add(stage); sprint.Items.Add(item);
        var assignment = JsonSerializer.Deserialize<W.WorkExecutionAssignmentV1>("{}")! with
        {
            OrganizationId = org, BoardId = project.BoardId, SprintId = sprint.SprintId, SprintExecutionId = sprint.Id,
            PolicyRevisionId = sprint.PolicyRevisionId, ItemExecutionId = item.Id, ItemId = ticket.Id,
            StageExecutionId = stage.Id, StageKey = stage.StageKey, AttemptId = Guid.NewGuid(), Attempt = 1,
            Item = JsonSerializer.SerializeToElement(new {}), Input = JsonSerializer.SerializeToElement(new {}),
            PriorOutcomes = [], Evidence = []
        };
        if (workspace) { stage.Status = WorkStageExecutionStatus.Running; item.Status = WorkItemExecutionStatus.Running; }
        switch (scenario)
        {
            case "archived-ticket": ticket.ArchivedAt = DateTimeOffset.UtcNow; break;
            case "foreign-ticket": ticket.OrganizationId = Guid.NewGuid(); break;
            case "wrong-agent": stage.AgentInstallationId = Guid.NewGuid(); break;
            case "wrong-employee": stage.OrganizationUserId = Guid.NewGuid(); break;
            case "wrong-organization": assignment = assignment with { OrganizationId = Guid.NewGuid() }; break;
            case "wrong-item": assignment = assignment with { ItemId = Guid.NewGuid() }; break;
            case "wrong-board": assignment = assignment with { BoardId = Guid.NewGuid() }; break;
            case "wrong-policy": assignment = assignment with { PolicyRevisionId = Guid.NewGuid() }; break;
            case "wrong-stage": assignment = assignment with { StageExecutionId = Guid.NewGuid() }; break;
            case "wrong-traversal": assignment = assignment with { Traversal = 2 }; break;
            case "wrong-attempt": assignment = assignment with { Attempt = 2 }; break;
            case "cancelled": sprint.Status = WorkSprintExecutionStatus.Cancelled; break;
            case "completed-stage": stage.Status = WorkStageExecutionStatus.Completed; break;
            case "missing-participant": (await db.ProjectParticipants.SingleAsync(x => x.OrganizationUserId == engineer.Id)).RemovedAt = DateTimeOffset.UtcNow; break;
        }
        if (condition is "valid-queued" or "wrong-queued-attempt" or "valid-dispatching" or "wrong-dispatching-attempt")
        {
            stage.Status = condition.Contains("dispatching") ? WorkStageExecutionStatus.Dispatching : WorkStageExecutionStatus.Running;
            item.Status = condition.Contains("dispatching") ? WorkItemExecutionStatus.Pending : WorkItemExecutionStatus.Running;
            stage.Attempts.Add(new WorkExecutionAttempt { Id = condition is "valid-queued" or "valid-dispatching" ? assignment.AttemptId : Guid.NewGuid(),
                StageExecutionId = stage.Id, Attempt = 1, AgentWorkItemId = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Pending });
        }
        if (workspace && scenario != "no-attempt")
            stage.Attempts.Add(new WorkExecutionAttempt { Id = assignment.AttemptId, StageExecutionId = stage.Id,
                Attempt = 1, AgentWorkItemId = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Running });
        db.AddRange(ticket, sprint); await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var capability = condition == "legacy-capability" ? "legacy.development.v1" : W.WorkManagementCapabilityNames.ExecutionRunV1;
        Func<Task> act = workspace
            ? () => new CSweet.AgentHost.Broker.ProjectCapabilityPolicy(db, policy).ValidateAsync(org,
                engineer.AgentInstallationId.Value, "git.workspace.prepare.v2", payload, default)
            : () => policy.RequireCapabilityWorkAsync(org, engineer.AgentInstallationId.Value, capability, payload, default);
        if (scenario is "valid" or "valid-queued" or "valid-dispatching") await act();
        else if (scenario == "missing-participant") await Assert.ThrowsAsync<InvalidOperationException>(act);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(act);
        Assert.Null(ticket.AssignedEmployeeId);
        Assert.Null(ticket.AssignedAgentInstallationId);
    }
}
