using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

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

    private static WorkOrchestrationStageDefinition Stage(
        string key, string type, Guid? column = null, bool successful = false) =>
        new(key, key, type, column, "", "{}", "{}", 60, null, Retry, null, successful);
}
