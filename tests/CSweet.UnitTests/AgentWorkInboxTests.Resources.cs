using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkInboxTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Checkpointed_handoff_replays_once_and_resumes_same_operation_after_old_runtime_stops(bool memoryFailure, bool validCheckpoint, bool personalChild)
    {
        await using var db = CreateDb(); var now = DateTimeOffset.UtcNow;
        var installation = Installation(now); installation.BusinessId = Guid.NewGuid().ToString();
        installation.Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, GrantRevision = 1 };
        installation.PackageVersion = new() { Id = installation.PackageVersionId, ManifestJson = """{"runtime":{"environmentProfile":"software-development-polyglot-v1","workspaceAccess":"ReadWrite"}}""" };
        var runtime = Runtime(installation, now); runtime.AllocatedMemoryMb = 4096;
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now); runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now); runtime.TransitionTo(AgentRuntimeStatus.Running, now);
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(installation.BusinessId), Kind = WorkBoardKind.Standard };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = board.OrganizationId, Board = board,
            Kind = WorkItemKind.Task, Status = WorkTaskStatus.Running, AssignedAgentInstallationId = installation.Id, AssignmentRevision = 1 };
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = board.OrganizationId, BoardId = board.Id, Status = WorkSprintExecutionStatus.Active };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItem = ticket, SprintExecution = sprint, CurrentStageKey = "implementation" };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecution = execution, AgentInstallationId = installation.Id, StageKey = "implementation" };
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = board.OrganizationId,
            AgentInstallationId = installation.Id, WorkItemId = ticket.Id, AssignmentRevision = 1, RepositoryId = Guid.NewGuid(),
            CheckpointRuntimeId = runtime.Id, CheckpointAt = now, CheckpointDigest = new('a', 64), CheckpointCommandClass = "build", CheckpointInputFingerprint = new('b', 64) };
        if (personalChild)
        {
            board.Kind = WorkBoardKind.Personal;
            var child = new WorkTask { Id = Guid.NewGuid(), OrganizationId = board.OrganizationId, Board = board,
                Kind = WorkItemKind.Task, Status = WorkTaskStatus.Running, AssignedAgentInstallationId = installation.Id,
                AssignmentRevision = 1, PlanningSpecificationJson = JsonSerializer.Serialize(new { personalPlan = new { rootItemId = ticket.Id } }) };
            workspace.WorkItemId = child.Id;
            db.Add(child);
        }
        db.AddRange(installation, runtime, stage, workspace, new AgentRuntimeGlobalSettings { Id = Guid.NewGuid() }); await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), new MutableTimeProvider(now));
        var work = await inbox.EnqueueAsync(installation.BusinessId, installation.Id, CSweet.Domain.Setup.AgentWorkKind.Capability,
            "work.execution.run", Json("{}"), "sizing-test", now.AddHours(1), sourceType: "WorkStageExecution", sourceId: stage.Id.ToString(), maximumAttempts: 1);
        db.WorkExecutionAttempts.Add(new() { Id = Guid.NewGuid(), StageExecutionId = stage.Id, AgentWorkItemId = work.Id, Status = WorkExecutionAttemptStatus.Running }); await db.SaveChangesAsync();
        var session = Session(installation, runtime); session.GrantRevision = 1;
        var claim = (await inbox.ClaimAsync(session, default))!;
        Assert.Equal(4096, claim.Resources!.MemoryMb);
        var request = new WorkloadResourceRequest(8192, new(workspace.Id, 1, workspace.CheckpointDigest), "build", workspace.CheckpointInputFingerprint, "project-estimate");
        var result = AgentWorkResult.ResourceHandoff(request);
        var completion = new AgentWorkCompletion(false, result.Value, result.Error, result.FailureCode, true);
        if (!validCheckpoint)
        {
            workspace.CheckpointDigest = new('c', 64); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.CompleteAsync(session, work.Id, claim.Attempt, claim.LeaseToken, completion, default));
            Assert.Equal(AgentRuntimeStatus.Running, runtime.Status); Assert.Equal(AgentWorkStatus.Leased, work.Status);
            Assert.Equal(0, work.ResourceReplacementCount); Assert.Empty(await db.WorkloadResourceProfiles.ToArrayAsync());
            return;
        }
        if (memoryFailure)
        {
            runtime.ResourceFailureCode = "workload-memory-limit-exceeded"; await db.SaveChangesAsync();
            await inbox.RecoverResourceFailureAsync(runtime.Id, default);
            await inbox.RecoverResourceFailureAsync(runtime.Id, default);
        }
        else
        {
            var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var upload = RuntimeCheckpointGate.ExecuteAsync(db, installation.Id, installation.BusinessId, runtime.Id, 1,
                async () => { uploadStarted.SetResult(); await finishUpload.Task; return true; }, default);
            await uploadStarted.Task;
            var handoff = inbox.CompleteAsync(session, work.Id, claim.Attempt, claim.LeaseToken, completion, default);
            Assert.False(handoff.IsCompleted); // An already-authorized upload must settle before its VM is fenced.
            finishUpload.SetResult(); await upload; await handoff;
            await inbox.CompleteAsync(session, work.Id, claim.Attempt, claim.LeaseToken, completion, default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RuntimeCheckpointGate.ExecuteAsync(db, installation.Id,
                installation.BusinessId, runtime.Id, 1, () => Task.FromResult(true), default));
        }
        Assert.Equal(1, work.ResourceReplacementCount); Assert.Equal(8192, work.RequestedMemoryMb);
        Assert.Equal(AgentWorkStatus.Pending, work.Status); Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.Null(await inbox.ClaimAsync(session, default)); Assert.Single(await db.WorkloadResourceProfiles.ToArrayAsync());
        runtime.TransitionTo(AgentRuntimeStatus.Cancelled, now);
        var replacement = Runtime(installation, now); replacement.AllocatedMemoryMb = 8192; db.Add(replacement); await db.SaveChangesAsync();
        var resumed = await inbox.ClaimAsync(Session(installation, replacement), default);
        Assert.Equal(work.Id, resumed!.WorkId); Assert.Equal(8192, resumed.Resources!.MemoryMb);
        if (personalChild) Assert.Equal(ticket.Id, resumed.Resources.ResumeWorkItemId);
        Assert.Equal("Running", work.ResourceRecoveryState); Assert.Equal(2, work.AttemptCount);
    }
}
