using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

/// <summary>
/// A finished ticket waiting on the Producer's acceptance used to sit until his next periodic attention review
/// (five minutes), and every ticket that depended on it waited too. The orchestrator now wakes the approver.
/// </summary>
public sealed class DecisionWakeTests
{
    [Fact]
    public async Task NewManagerApprovalStage_WakesTheAgentApproverImmediately()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var producerInstallationId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ManagerOrganizationUserId = producerId,
            Key = "VGF", Name = "Game", CreatedAt = now, UpdatedAt = now
        };
        var task = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id, Title = "Architecture plan",
            Description = "", Status = WorkTaskStatus.Running, Priority = WorkTaskPriority.High, CreatedAt = now, UpdatedAt = now
        };
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = producerId, OrganizationId = organizationId, AgentInstallationId = producerInstallationId,
            DisplayName = "Producer", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true, CreatedAt = now
        });
        db.AgentInstallations.Add(new AgentInstallation { Id = producerInstallationId, CreatedAt = now, UpdatedAt = now });
        db.AgentSchedules.Add(new AgentSchedule
        {
            Id = Guid.NewGuid(), AgentInstallationId = producerInstallationId, IsEnabled = true,
            TickFrequencySeconds = 300, NextAttentionReviewAt = now.AddMinutes(5), MaxRuntimeSeconds = 600
        });
        db.WorkOrchestrationPolicyRevisions.Add(new WorkOrchestrationPolicyRevision
        {
            Id = policyId, OrganizationId = organizationId, BoardId = board.Id, PolicyId = Guid.NewGuid(),
            Revision = 1, Name = "Delivery", InitialStageKey = "ready", IsPublished = true, CreatedAt = now
        });
        db.WorkOrchestrationStages.AddRange(
            new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = policyId, Key = "ready", Name = "Ready", Type = WorkOrchestrationStageType.Queue },
            new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = policyId, Key = "producer-review", Name = "Producer Review", Type = WorkOrchestrationStageType.ManagerApproval });
        db.WorkOrchestrationTransitions.Add(new WorkOrchestrationTransition
        {
            Id = Guid.NewGuid(), PolicyRevisionId = policyId, FromStageKey = "ready", OutcomeCode = "ready", ToStageKey = "producer-review"
        });
        var execution = new WorkSprintExecution
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id, SprintId = Guid.NewGuid(),
            PolicyRevisionId = policyId, StartedByOrganizationUserId = producerId,
            Status = WorkSprintExecutionStatus.Active, StartedAt = now, UpdatedAt = now
        };
        var item = new WorkItemExecution
        {
            Id = Guid.NewGuid(), SprintExecutionId = execution.Id, WorkItemId = task.Id, ItemIdentifier = "VGF-5",
            CurrentStageKey = "ready", Status = WorkItemExecutionStatus.Pending, CreatedAt = now, UpdatedAt = now
        };
        var stage = new WorkStageExecution
        {
            Id = Guid.NewGuid(), ItemExecutionId = item.Id, StageKey = "ready", StageType = WorkOrchestrationStageType.Queue,
            Status = WorkStageExecutionStatus.Pending, PrincipalKind = WorkOrchestrationPrincipalKind.PlatformAction,
            CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(board, task, execution, item, stage);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var orchestrator = new WorkOrchestrator(db, null!, new AgentAttentionInvalidationService(db, TimeProvider.System),
            null!, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        await orchestrator.PulseAsync();

        db.ChangeTracker.Clear();
        var review = await db.WorkStageExecutions.SingleAsync(x => x.StageKey == "producer-review");
        Assert.Equal(WorkStageExecutionStatus.WaitingForApproval, review.Status);
        var schedule = await db.AgentSchedules.SingleAsync();
        Assert.Equal("work.approval-requested", schedule.PendingAttentionTriggerCategory);
        Assert.Equal(review.Id, schedule.PendingAttentionCorrelationId);
        Assert.True(schedule.NextAttentionReviewAt <= DateTimeOffset.UtcNow);
    }
}
