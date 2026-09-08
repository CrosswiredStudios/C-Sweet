using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Theory]
    [InlineData("Completed")]
    [InlineData("Blocked")]
    [InlineData("Failed")]
    public async Task TrustedMergeOutputSurvivesPersistenceAndScopedBrokerRead(string disposition)
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var request = await SeedApprovalAsync(db, setup, true);
        Grant(db, setup, WorkOrchestrationActions.Read, GrantScopeKind.Board, request.BoardId);
        var execution = await db.WorkSprintExecutions.Include(x => x.Items).ThenInclude(x => x.WorkItem)
            .Include(x => x.Items).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts).SingleAsync();
        var policy = await db.WorkOrchestrationPolicyRevisions.Include(x => x.Stages).Include(x => x.Transitions).SingleAsync();
        var stage = Assert.Single(Assert.Single(execution.Items).Stages);
        stage.StageType = WorkOrchestrationStageType.TrustedPlatformAction;
        stage.PlatformAction = "source-control.merge.execute.v2";
        stage.Status = WorkStageExecutionStatus.Pending;
        var merge = new TrustedMergeFixture(disposition);
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [merge], TimeProvider.System, null!);
        await orchestrator.ExecuteTrustedActionAsync(execution, policy, stage, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var attempt = Assert.Single(await db.WorkExecutionAttempts.ToListAsync());
        Assert.Contains(new string('b',40), attempt.ResultJson);
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var result = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.Read), WorkOrchestrationActions.Read,
            new Wire.ReadWorkOrchestrationRequest(request.BoardId, SprintExecutionId: request.SprintExecutionId));
        Assert.True(result.Succeeded, result.Error);
        var response = JsonSerializer.Deserialize<Wire.WorkSprintExecutionResponse>(result.Payload.ToByteArray(), JsonOptions)!;
        var evidence = Assert.Single(response.Items).Stages.Single(x => x.Id == stage.Id).LatestOutcome;
        if (disposition == "Completed")
        {
            Assert.NotNull(evidence);
            Assert.Equal(attempt.Id, evidence.AttemptId);
            Assert.Equal(new string('b',40), evidence.Output.GetProperty("mergeCommitSha").GetString());
            Assert.Equal(new string('a',40), evidence.Output.GetProperty("sourceCommitSha").GetString());
        }
        else Assert.Null(evidence);
    }

    private sealed class TrustedMergeFixture(string disposition) : ITrustedWorkActionExecutor
    {
        public string Action => "source-control.merge.execute.v2";
        public Task<TrustedWorkActionResult> ExecuteAsync(TrustedWorkActionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrustedWorkActionResult(disposition, "approved", "Exact merge result",
                JsonSerializer.SerializeToElement(new { sourceCommitSha = new string('a',40), mergeCommitSha = new string('b',40) }), []));
    }
}
