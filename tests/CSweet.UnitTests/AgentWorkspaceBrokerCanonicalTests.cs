using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkspaceBrokerTests
{
    [Theory]
    [InlineData("prepare", "valid")]
    [InlineData("snapshot", "valid")]
    [InlineData("snapshot", "revoked-grant")]
    [InlineData("inspect", "valid")]
    [InlineData("publish", "valid")]
    [InlineData("prepare", "wrong-agent")]
    [InlineData("prepare", "stale-traversal")]
    [InlineData("prepare", "cancelled")]
    [InlineData("prepare", "archived-ticket")]
    [InlineData("prepare", "no-attempt")]
    [InlineData("prepare", "wrong-repository")]
    [InlineData("publish", "revoked-grant")]
    [InlineData("publish", "quality")]
    [InlineData("inspect", "missing-participant")]
    public async Task Canonical_stage_workspace_requires_current_attempt_repository_and_action(string operation, string scenario)
    {
        await using var db = CreateDb(); var seed = await SeedAsync(db); var request = seed.Request;
        var employee = await db.CoreOrganizationUsers.SingleAsync();
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        db.Add(human); await db.SaveChangesAsync();
        var project = await new ProjectSetupService(db, TimeProvider.System, new ProjectWorkPolicy(db, TimeProvider.System))
            .CreateAsync(human, new("Game", "Build", human.Id, null, [employee.Id], null, null, null, "canonical-test"), default);
        var board = await db.WorkBoards.SingleAsync(x => x.Id == project.BoardId);
        var workspace = await db.SourceControlWorkspaces.SingleAsync(); workspace.TeamId = board.TeamId!.Value;
        (await db.TeamRepositoryPolicies.SingleAsync()).TeamId = workspace.TeamId;
        var ticket = await db.CoreWorkTasks.SingleAsync(x => x.Id == request.WorkItemId);
        ticket.BoardId = board.Id; ticket.AssignedAgentInstallationId = null;
        ticket.DeliverySpecificationJson = JsonSerializer.Serialize(new { repositoryId = scenario == "wrong-repository" ? Guid.NewGuid() : request.RepositoryId });
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId, BoardId = board.Id,
            Status = scenario == "cancelled" ? WorkSprintExecutionStatus.Cancelled : WorkSprintExecutionStatus.Active };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, SprintExecutionId = sprint.Id,
            CurrentStageKey = scenario == "quality" ? "quality" : "specialist-execution", Status = WorkItemExecutionStatus.Running };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = item.Id, StageKey = item.CurrentStageKey,
            Status = WorkStageExecutionStatus.Running, StageType = WorkOrchestrationStageType.AgentExecution,
            PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation, OrganizationUserId = employee.Id,
            AgentInstallationId = scenario == "wrong-agent" ? Guid.NewGuid() : employee.AgentInstallationId,
            Traversal = scenario == "stale-traversal" ? 1 : 0 };
        if (scenario != "no-attempt") stage.Attempts.Add(new() { Id = Guid.NewGuid(), StageExecutionId = stage.Id,
            AgentWorkItemId = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Running });
        item.Stages.Add(stage); sprint.Items.Add(item); db.Add(sprint);
        var action = operation switch { "prepare" or "snapshot" => GitWorkspaceCapabilities.Prepare, "publish" => GitWorkspaceCapabilities.Publish, _ => GitWorkspaceCapabilities.Inspect };
        db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = request.AgentInstallationId,
            ScopeKind = GrantScopeKind.WorkItem, ScopeId = ticket.Id, Action = action,
            GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity, GrantedBySubjectId = sprint.Id,
            RevokedAt = scenario == "revoked-grant" ? DateTimeOffset.UtcNow : null });
        if (scenario == "archived-ticket") ticket.ArchivedAt = DateTimeOffset.UtcNow;
        if (scenario == "missing-participant") (await db.ProjectParticipants.SingleAsync(x => x.OrganizationUserId == employee.Id)).RemovedAt = DateTimeOffset.UtcNow;
        if (operation is not ("prepare" or "snapshot")) { workspace.Status = SourceControlWorkspaceStatus.Ready; workspace.WorkspaceKey = "opaque"; workspace.BaseCommitSha = new string('a',40); }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (operation == "snapshot")
        {
            var root = Path.Combine(Path.GetTempPath(), "canonical-snapshot-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bridge = new WorkspaceVolumeBridge(db, new CSweet.TrustedServices.WorkspaceArtifactValidator(),
                    Microsoft.Extensions.Options.Options.Create(new CSweet.Infrastructure.Setup.AgentRuntimeManagerOptions { WorkspaceSnapshotStorePath = root }));
                using var bytes = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true))
                { using var writer = new StreamWriter(zip.CreateEntry("README.md").Open()); writer.Write("Stage-authored source"); }
                bytes.Position = 0;
                var lease = new WorkspaceVolumeLease(request.OrganizationId, request.AgentInstallationId, request.WorkspaceId, request.WorkItemId, request.AssignmentRevision);
                if (scenario == "valid")
                {
                    await bridge.ImportAsync(lease, bytes);
                    var current = await db.SourceControlWorkspaces.SingleAsync(); current.Status = SourceControlWorkspaceStatus.Ready;
                    (await db.ScopedActionGrants.SingleAsync(x => x.ScopeId == ticket.Id)).Action = GitWorkspaceCapabilities.Inspect;
                    await db.SaveChangesAsync();
                    Assert.Equal(1, (await bridge.ExportAsync(lease)).Manifest.FileCount);
                    (await db.WorkStageExecutions.SingleAsync()).Status = WorkStageExecutionStatus.Completed; await db.SaveChangesAsync();
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bridge.ExportAsync(lease));
                }
                else { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bridge.ImportAsync(lease, bytes)); Assert.False(Directory.Exists(root)); }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            return;
        }
        var host = new FakeHost(); var volumes = new FakeVolumes(); var broker = new AgentWorkspaceBroker(db, host, volumes, WorkspaceSyncTestOptions.Value);
        Func<Task> act = operation == "prepare" ? async () => { await broker.PrepareAsync(request); }
            : async () => { await broker.ExecuteAsync(new(request.OrganizationId, request.RepositoryId, request.WorkspaceId,
                request.WorkItemId, request.AssignmentRevision, "opaque", "op", operation), "http://localhost"); };
        if (scenario == "valid") { await act(); Assert.True(host.Request is not null || host.GitHubOperation is not null); }
        else { await Assert.ThrowsAnyAsync<Exception>(act); Assert.Null(host.Request); Assert.Null(host.GitHubOperation); Assert.Null(volumes.Lease); }
    }
}
