using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PersonalDevelopmentWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_claimed_ticket_uses_Core_broker_and_retries_same_repository_after_lost_response(bool loseFirstResponse)
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
        var internalHost = DispatchProxy.Create<ITrustedSourceControlHostClient, SourceHost>();
        var source = (SourceHost)internalHost;
        source.LoseNextResponse = loseFirstResponse;
        var core = new PersonalRepositoryBroker(f.Db, internalHost);
        using var http = new HttpClient(new CoreTransport(core)) { BaseAddress = new Uri("http://core/") };
        var host = new Host(new CoreWorkspaceBrokerClient(http));
        // AgentHost has no ITrustedSourceControlHostClient or GitHost credentials.
        var handler = new GitWorkspaceCapabilityHandler(f.Db, host, null!, null!);
        var session = new AgentSession("session", "developer", f.Installation.ToString(), f.Organization.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { capability, GitWorkspaceCapabilities.Prepare }, 1));
        var request = new RequestCapability { RequestId = "personal", Capability = capability, ContentType = "application/json",
            Payload = JsonPayload.From(new { itemId = item.Id, idempotencyKey = "personal-prepare" }) };
        async Task<CapabilityResult> Send()
        {
            var results = new List<CapabilityResult>(); await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
            return Assert.Single(results);
        }
        if (loseFirstResponse)
        {
            Assert.False((await Send()).Succeeded);
            Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
            Assert.Empty(await f.Db.SourceControlWorkspaces.ToListAsync());
        }
        var first = await Send(); Assert.True(first.Succeeded, first.Error);
        var second = await Send(); Assert.True(second.Succeeded, second.Error);
        var repository = Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
        Assert.True(repository.IsPrivate); Assert.Single(await f.Db.SourceControlWorkspaces.ToListAsync()); Assert.Equal(1, host.Prepares);
        Assert.Equal(1, item.AssignmentRevision); Assert.Equal(f.Installation, item.AssignedAgentInstallationId);
        Assert.Equal(1, item.Revision); // Source binding must not invalidate the SDK-owned queue claim.
        Assert.Single(source.RepositoryIds.Distinct());
        var brokerRequest = new AgentBrokerPersonalRepositoryRequest(f.Organization, f.Installation, item.Id, "personal-prepare");
        repository.Status = SourceControlRepositoryStatus.Provisioning; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => core.CreateAsync(brokerRequest with { AgentInstallationId = Guid.NewGuid() }, default));
        var policy = await f.Db.RepositoryProvisioningPolicies.SingleAsync(); policy.IsEnabled = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => core.CreateAsync(brokerRequest, default));
        policy.IsEnabled = true;
        item.ClaimExpiresAt = null; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => core.CreateAsync(brokerRequest, default));
        Assert.False((await Send()).Succeeded); Assert.Equal(1, host.Prepares);
        Assert.Equal(loseFirstResponse ? 2 : 1, source.RepositoryIds.Count);
    }

    public class SourceHost : DispatchProxy
    {
        public bool LoseNextResponse;
        public List<Guid> RepositoryIds { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "ExecuteInternalAsync") throw new NotSupportedException();
            var request = Assert.IsType<InternalGitRepositoryRequest>(args![0]);
            Assert.Equal("create", request.Operation); RepositoryIds.Add(request.RepositoryId);
            if (LoseNextResponse) { LoseNextResponse = false; throw new InvalidOperationException("GitHost response lost after create"); }
            return Task.FromResult<InternalGitRepositoryInspection>(null!);
        }
    }
    private sealed class CoreTransport(PersonalRepositoryBroker core) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/agent-broker/v2/workspaces/personal-repository", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            var json = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(new[] { "agentInstallationId", "idempotencyKey", "organizationId", "workItemId" }, json.EnumerateObject().Select(x => x.Name).Order().ToArray());
            try
            {
                await core.CreateAsync(json.Deserialize<AgentBrokerPersonalRepositoryRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!, ct);
                return new(HttpStatusCode.NoContent);
            }
            catch (InvalidOperationException) { return new(HttpStatusCode.Conflict); }
        }
    }
    private sealed class Host(CoreWorkspaceBrokerClient core) : ITrustedGitHostClient
    {
        public int Prepares;
        public Task CreatePersonalRepositoryAsync(AgentBrokerPersonalRepositoryRequest r, CancellationToken ct) => core.CreatePersonalRepositoryAsync(r, ct);
        public Task<TrustedWorkspaceMaterialization> PrepareAsync(TrustedWorkspacePrepareRequest r, CancellationToken ct)
        { Prepares++; return Task.FromResult(new TrustedWorkspaceMaterialization("personal-workspace", $"/workspace/{r.WorkItemId:N}/1", new string('a', 40), false)); }
        public Task<TrustedWorkspaceRefresh> RefreshAsync(TrustedWorkspaceOperationRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitWorkspaceInspection> InspectAsync(TrustedWorkspaceOperationRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<TrustedWorkspacePublication> PublishAsync(TrustedWorkspacePublishRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitWorkspaceCleanupResult> CleanupAsync(TrustedWorkspaceCleanupRequest r, CancellationToken ct) => throw new NotSupportedException();
    }
}
