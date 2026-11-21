using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.SourceControl;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Fixture = CSweet.UnitTests.HierarchicalDeliveryServiceTests.Fixture;

namespace CSweet.DeliveryWorkflow.Tests;

public sealed partial class AgentDeliveryJourneyTests
{
    // The journey uses human completion to advance the orchestrator. Give its QA callback the
    // running installation and retained Ready workspace that the real broker sees on a retry.
    private static async Task<WorkExecutionOutcomeV1> RunWithRetainedQaWorkspaceAsync(Fixture f,
        AgentTestRuntime runtime, Guid workspace, SourceControlPublication publication, string commit,
        WorkExecutionAssignment assignment, Func<Task<WorkExecutionOutcomeV1>> execute)
    {
        var installation = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var stage = await f.Db.WorkStageExecutions.SingleAsync(s => s.ItemExecution!.WorkItemId == assignment.TaskItemId &&
            s.StageKey == "quality" && s.Status == WorkStageExecutionStatus.WaitingForHuman);
        var qaWorkspace = new SourceControlWorkspace { Id = workspace, OrganizationId = f.Org, TeamId = f.Board.TeamId!.Value,
            WorkItemId = assignment.TaskItemId, AssignmentRevision = assignment.AssignmentRevision, RepositoryId = publication.RepositoryId,
            AgentInstallationId = installation, Status = SourceControlWorkspaceStatus.Ready, BaseCommitSha = commit,
            BranchName = publication.TicketBranch, IntegrationTargetBranch = publication.TargetBranch, WorkspaceKey = workspace.ToString("N") };
        var actions = new HashSet<string> { GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Inspect };
        var grants = actions.Select(action => new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installation, Action = action,
            ScopeKind = GrantScopeKind.WorkItem, ScopeId = assignment.TaskItemId }).ToArray();
        f.Db.Add(qaWorkspace);
        f.Db.AddRange(grants);
        f.Qa.AgentInstallationId = stage.AgentInstallationId = installation;
        stage.Status = WorkStageExecutionStatus.Running;
        await f.Db.SaveChangesAsync();
        try
        {
            var handler = new GitWorkspaceCapabilityHandler(f.Db, new UnavailableTrustedGitHostClient(),
                new ScopedActionAuthorizationService(f.Db), null!, Options.Create(new WorkspaceSyncTransferOptions { AgentGitDelivery = true }));
            var session = new AgentSession("fixture", "qa", installation.ToString(), f.Org.ToString(), "runtime", "tick",
                new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), actions, 1));
            async Task<CapabilityResult> Invoke(string capability, object payload, CancellationToken ct)
            {
                var input = new RequestCapability { RequestId = "review", Capability = capability, Payload = JsonPayload.From(payload, Json) };
                var replies = new List<CapabilityResult>();
                await foreach (var response in handler.HandleAsync(session, input, ct)) replies.Add(response);
                return Assert.Single(replies);
            }
            var stale = await Invoke(GitWorkspaceCapabilities.Inspect, new InspectGitWorkspaceRequest(workspace, assignment.AssignmentRevision)
                { PushedCommitSha = publication.CommitSha }, default);
            Assert.False(stale.Succeeded);
            Assert.Contains("differs from the assigned QA commit", stale.Error);
            runtime.RegisterCapability<PrepareGitWorkspaceRequest,GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, async (request, ct) =>
            {
                var response = await Invoke(GitWorkspaceCapabilities.Prepare, request, ct);
                Assert.True(response.Succeeded, response.Error);
                var prepared = response.Payload.ToElement().Deserialize<GitWorkspaceResult>(Json)!;
                Assert.True(prepared.Resumed);
                Assert.Equal(commit, prepared.BaseCommitSha);
                Assert.Null(prepared.BranchName);
                return prepared;
            });
            return await execute();
        }
        finally
        {
            f.Qa.AgentInstallationId = stage.AgentInstallationId = null;
            stage.Status = WorkStageExecutionStatus.WaitingForHuman;
            f.Db.RemoveRange(grants);
            f.Db.Remove(qaWorkspace);
            await f.Db.SaveChangesAsync();
        }
    }
}
