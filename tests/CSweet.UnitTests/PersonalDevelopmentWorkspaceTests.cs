using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PersonalDevelopmentWorkspaceTests
{
    [Fact]
    public async Task Owned_claimed_ticket_gets_one_private_repository_and_revoked_claim_blocks_reuse()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        const string capability = "source-control.personal-work.prepare.v1";
        var approval = await f.Db.AgentInstallationGrants.SingleAsync(); approval.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { capability, GitWorkspaceCapabilities.Prepare });
        var actor = await f.Db.CoreOrganizationUsers.SingleAsync(); actor.DisplayName = "Daniel";
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Organization, Kind = WorkBoardKind.Personal, OwnerOrganizationUserId = actor.Id };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Organization, BoardId = board.Id, Board = board,
            Title = "Build a puzzle", Description = "Create a puzzle application and test it.", Status = WorkTaskStatus.Running,
            ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            SourceConversationId = Guid.NewGuid(), SourceMessageId = Guid.NewGuid() };
        f.Db.WorkBoards.Add(board); f.Db.CoreWorkTasks.Add(item); await f.Db.SaveChangesAsync();
        var host = new Host();
        var internalHost = DispatchProxy.Create<ITrustedSourceControlHostClient, SourceHost>();
        var handler = new GitWorkspaceCapabilityHandler(f.Db, host, null!, null!, internalHost);
        var session = new AgentSession("session", "developer", f.Installation.ToString(), f.Organization.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { capability, GitWorkspaceCapabilities.Prepare }, 1));
        var request = new RequestCapability { RequestId = "personal", Capability = capability, ContentType = "application/json",
            Payload = JsonPayload.From(new { itemId = item.Id, idempotencyKey = "personal-prepare" }) };
        async Task<CapabilityResult> Send()
        {
            var results = new List<CapabilityResult>(); await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
            return Assert.Single(results);
        }
        var first = await Send(); Assert.True(first.Succeeded, first.Error);
        var second = await Send(); Assert.True(second.Succeeded, second.Error);
        var repository = Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
        Assert.True(repository.IsPrivate); Assert.Single(await f.Db.SourceControlWorkspaces.ToListAsync()); Assert.Equal(1, host.Prepares);
        Assert.Equal(1, item.AssignmentRevision); Assert.Equal(f.Installation, item.AssignedAgentInstallationId);
        Assert.Equal(1, item.Revision); // Source binding must not invalidate the SDK-owned queue claim.
        item.ClaimExpiresAt = null; await f.Db.SaveChangesAsync();
        Assert.False((await Send()).Succeeded); Assert.Equal(1, host.Prepares);
    }

    public class SourceHost : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "ExecuteInternalAsync"
            ? Task.FromResult<InternalGitRepositoryInspection>(null!) : throw new NotSupportedException();
    }
    private sealed class Host : ITrustedGitHostClient
    {
        public int Prepares;
        public Task<TrustedWorkspaceMaterialization> PrepareAsync(TrustedWorkspacePrepareRequest r, CancellationToken ct)
        { Prepares++; return Task.FromResult(new TrustedWorkspaceMaterialization("personal-workspace", $"/workspace/{r.WorkItemId:N}/1", new string('a', 40), false)); }
        public Task<TrustedWorkspaceRefresh> RefreshAsync(TrustedWorkspaceOperationRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitWorkspaceInspection> InspectAsync(TrustedWorkspaceOperationRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<TrustedWorkspacePublication> PublishAsync(TrustedWorkspacePublishRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitWorkspaceCleanupResult> CleanupAsync(TrustedWorkspaceCleanupRequest r, CancellationToken ct) => throw new NotSupportedException();
    }
}
