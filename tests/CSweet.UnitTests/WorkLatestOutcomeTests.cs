using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkLatestOutcomeTests
{
    [Theory]
    [InlineData("Completed")]
    [InlineData("Blocked")]
    [InlineData("Failed")]
    public void ReadReturnsExactValidatedAttemptForTerminalDisposition(string disposition)
    {
        var stage = new Wire.WorkStageExecutionResponse(Guid.NewGuid(), "specialist-execution", "AgentExecution", 0,
            disposition, "AgentInstallation", Guid.NewGuid(), Guid.NewGuid(), null, 2,
            "result", "Current result", "Stale error", null, DateTimeOffset.UtcNow);
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Completed };
        var outcome = new Wire.WorkExecutionOutcomeV1(stage.Id, attempt.Id, disposition, "result", "Current result",
            JsonSerializer.SerializeToElement(new {}), [], []);
        attempt.ResultJson = JsonSerializer.Serialize(outcome, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(outcome.Summary, WorkManagementCapabilityHandler.ReadLatestOutcome(stage, attempt)?.Summary);
        Assert.Null(WorkManagementCapabilityHandler.ReadLatestOutcome(stage with { Id = Guid.NewGuid() }, attempt));
        Assert.Null(WorkManagementCapabilityHandler.ReadLatestOutcome(stage with { Status = "Running" }, attempt));
        Assert.Null(WorkManagementCapabilityHandler.ReadLatestOutcome(stage with { LastOutcomeCode = "other" }, attempt));
        attempt.Status = WorkExecutionAttemptStatus.Failed;
        Assert.Null(WorkManagementCapabilityHandler.ReadLatestOutcome(stage, attempt));
    }
}
