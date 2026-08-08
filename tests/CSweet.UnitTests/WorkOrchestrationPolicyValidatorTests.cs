using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using CSweet.Domain.WorkManagement;

namespace CSweet.UnitTests;

public sealed class WorkOrchestrationPolicyValidatorTests
{
    private static readonly WorkOrchestrationRetryPolicy Retry = new();

    [Fact]
    public void ValidSoftwareGraph_WithBoundedQaReturn_IsAccepted()
    {
        var column = Guid.NewGuid();
        var errors = WorkOrchestrationPolicyValidator.Validate(
            "ready", WorkMergeModes.ManagerApproval,
            new(100, 25, 10, 5, 1),
            [
                Stage("ready", WorkOrchestrationStageTypes.Queue, column),
                Stage("development", WorkOrchestrationStageTypes.AgentExecution, column),
                Stage("quality", WorkOrchestrationStageTypes.AgentExecution, column),
                Stage("done", WorkOrchestrationStageTypes.Terminal, column, true)
            ],
            [
                new("ready", "ready", "development"),
                new("development", "completed", "quality"),
                new("quality", "changes_requested", "development", 3),
                new("quality", "passed", "done")
            ],
            new HashSet<Guid> { column });

        Assert.Empty(errors);
    }

    [Fact]
    public void BrokerSoftwareTemplate_IsAccepted()
    {
        var ready = Guid.NewGuid();
        var development = Guid.NewGuid();
        var devComplete = Guid.NewGuid();
        var quality = Guid.NewGuid();
        var readyToMerge = Guid.NewGuid();
        var done = Guid.NewGuid();
        var columns = new HashSet<Guid>
        {
            ready, development, devComplete, quality, readyToMerge, done
        };
        var errors = WorkOrchestrationPolicyValidator.Validate(
            "ready", WorkMergeModes.ManagerApproval,
            new(100, 25, 10, 5, 1),
            [
                Stage("ready", WorkOrchestrationStageTypes.Queue, ready),
                Stage("development", "MemberExecution", development),
                Stage("dev-complete", WorkOrchestrationStageTypes.Queue, devComplete),
                Stage("quality", "MemberExecution", quality),
                Stage("merge-decision", WorkOrchestrationStageTypes.ManagerApproval, readyToMerge),
                new("governed-merge", "Governed merge", WorkOrchestrationStageTypes.TrustedPlatformAction,
                    readyToMerge, "", "{}", "{}", 300, 1, Retry, "git.governed-merge.v1"),
                Stage("done", WorkOrchestrationStageTypes.Terminal, done, true),
                Stage("cancelled", WorkOrchestrationStageTypes.Terminal, done)
            ],
            [
                new("ready", "ready", "development"),
                new("development", "completed", "dev-complete"),
                new("dev-complete", "ready", "quality"),
                new("quality", "passed", "merge-decision"),
                new("quality", "changes_requested", "development", 3),
                new("merge-decision", "approved", "governed-merge"),
                new("merge-decision", "rejected", "cancelled"),
                new("governed-merge", "merged", "done")
            ],
            columns);

        Assert.True(errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(x => $"{x.Code}: {x.Message}")));
    }

    [Fact]
    public void UnboundedCycle_IsRejectedDeterministically()
    {
        var errors = WorkOrchestrationPolicyValidator.Validate(
            "a", WorkMergeModes.ManagerApproval,
            new(1, 1, 1, 1, 1),
            [
                Stage("a", WorkOrchestrationStageTypes.Queue),
                Stage("b", WorkOrchestrationStageTypes.AgentExecution),
                Stage("done", WorkOrchestrationStageTypes.Terminal, successful: true)
            ],
            [new("a", "ready", "b"), new("b", "again", "a"), new("b", "done", "done")],
            new HashSet<Guid>());

        Assert.Contains(errors, x => x.Code == "transition.unbounded_cycle");
    }

    [Fact]
    public void InvalidSchemaAndForeignColumn_AreRejected()
    {
        var errors = WorkOrchestrationPolicyValidator.Validate(
            "run", WorkMergeModes.Automatic,
            new(1, 1, 1, 1, 1),
            [
                new("run", "Run", WorkOrchestrationStageTypes.AgentExecution, Guid.NewGuid(),
                    "", "{", "{}", 60, null, Retry),
                Stage("done", WorkOrchestrationStageTypes.Terminal, successful: true)
            ],
            [new("run", "completed", "done")],
            new HashSet<Guid>());

        Assert.Contains(errors, x => x.Code == "stage.schema");
        Assert.Contains(errors, x => x.Code == "stage.column");
    }

    [Fact]
    public void MemberExecution_UsesTheExactAssigneeTypeForRuntimeState()
    {
        var humanItem = new WorkItemExecution { Id = Guid.NewGuid() };
        var agentItem = new WorkItemExecution { Id = Guid.NewGuid() };
        var humanId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var stage = new WorkOrchestrationStage
        {
            Id = Guid.NewGuid(),
            Key = "delivery",
            Name = "Delivery",
            Type = WorkOrchestrationStageType.MemberExecution
        };

        WorkOrchestrationService.CreateStageExecution(
            humanItem, stage,
            [new CSweet.Domain.WorkManagement.WorkItemStageAssignment
            {
                StageKey = "delivery",
                PrincipalKind = WorkOrchestrationPrincipalKind.Human,
                OrganizationUserId = humanId
            }], Guid.NewGuid(), DateTimeOffset.UtcNow);
        WorkOrchestrationService.CreateStageExecution(
            agentItem, stage,
            [new CSweet.Domain.WorkManagement.WorkItemStageAssignment
            {
                StageKey = "delivery",
                PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
                AgentInstallationId = installationId
            }], Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(WorkStageExecutionStatus.WaitingForHuman, Assert.Single(humanItem.Stages).Status);
        Assert.Equal(humanId, Assert.Single(humanItem.Stages).OrganizationUserId);
        Assert.Equal(WorkStageExecutionStatus.Pending, Assert.Single(agentItem.Stages).Status);
        Assert.Equal(installationId, Assert.Single(agentItem.Stages).AgentInstallationId);
    }

    private static WorkOrchestrationStageDefinition Stage(
        string key, string type, Guid? column = null, bool successful = false) =>
        new(key, key, type, column, "", "{}", "{}", 60, null, Retry, null, successful);
}
