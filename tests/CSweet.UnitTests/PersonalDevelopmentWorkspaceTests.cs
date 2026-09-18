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
        var epic = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Organization, BoardId = board.Id,
            ParentWorkTaskId = item.Id, Title = "Puzzle Quest MVP", Kind = WorkItemKind.Epic,
            Status = WorkTaskStatus.Backlog, IsExecutable = false, CreatedAt = DateTimeOffset.UtcNow };
        f.Db.WorkBoards.Add(board); f.Db.CoreWorkTasks.AddRange(item, epic); await f.Db.SaveChangesAsync();
        var internalHost = DispatchProxy.Create<ITrustedSourceControlHostClient, SourceHost>();
        var source = (SourceHost)internalHost;
        source.LoseNextResponse = loseFirstResponse;
        var core = new PersonalRepositoryBroker(f.Db, internalHost);
        using var http = new HttpClient(new CoreTransport(core)) { BaseAddress = new Uri("http://core/") };
        var host = new Host(new CoreWorkspaceBrokerClient(http));
        // AgentHost has no ITrustedSourceControlHostClient or GitHost credentials.
        var handler = new GitWorkspaceCapabilityHandler(f.Db, host, null!, null!, WorkspaceSyncTestOptions.Value);
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
        Assert.True(repository.IsPrivate); Assert.Equal("puzzle-quest-mvp", repository.Name);
        Assert.Single(await f.Db.SourceControlWorkspaces.ToListAsync()); Assert.Equal(1, host.Prepares);
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

    [Fact]
    public async Task Plan_tasks_have_distinct_branches_in_one_repository_and_later_stories_start_from_main()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var actions = new HashSet<string> { GitWorkspaceCapabilities.PreparePersonal, GitWorkspaceCapabilities.Prepare };
        (await f.Db.AgentInstallationGrants.SingleAsync()).RequiredCapabilitiesJson = JsonSerializer.Serialize(actions);
        var actor = await f.Db.CoreOrganizationUsers.SingleAsync();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Organization, Kind = WorkBoardKind.Personal, OwnerOrganizationUserId = actor.Id };
        var root = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Organization, BoardId = board.Id, Board = board,
            Title = "Breakout", Status = WorkTaskStatus.Running, ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), SourceConversationId = Guid.NewGuid(), SourceMessageId = Guid.NewGuid() };
        WorkTask Child(string title) => new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, BoardId = board.Id, Board = board,
            ParentWorkTaskId = Guid.NewGuid(), Title = title, Kind = WorkItemKind.Task, AssignedAgentInstallationId = f.Installation, Status = WorkTaskStatus.Running,
            PlanningSpecificationJson = JsonSerializer.Serialize(new CSweet.WorkManagement.Contracts.WorkItemPlanningSpecification([], []) {
                PersonalPlan = new(root.Id, 1, "Implementation") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var first = Child("Paddle"); var second = Child("Bricks");
        f.Db.WorkBoards.Add(board); f.Db.CoreWorkTasks.AddRange(root, first, second); await f.Db.SaveChangesAsync();
        var internalHost = DispatchProxy.Create<ITrustedSourceControlHostClient, SourceHost>();
        using var http = new HttpClient(new CoreTransport(new PersonalRepositoryBroker(f.Db, internalHost))) { BaseAddress = new Uri("http://core/") };
        var host = new Host(new CoreWorkspaceBrokerClient(http));
        var handler = new GitWorkspaceCapabilityHandler(f.Db, host, null!, null!, WorkspaceSyncTestOptions.Value);
        var session = new AgentSession("session", "developer", f.Installation.ToString(), f.Organization.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), actions, 1));
        async Task<GitWorkspaceResult> Prepare(WorkTask task)
        {
            var request = new RequestCapability { RequestId = "prepare", Capability = GitWorkspaceCapabilities.PreparePersonal, ContentType = "application/json",
                Payload = JsonPayload.From(new { itemId = root.Id, taskItemId = task.Id, idempotencyKey = task.Id.ToString() }) };
            await foreach (var result in handler.HandleAsync(session, request, default))
            {
                Assert.True(result.Succeeded, result.Error);
                return JsonSerializer.Deserialize<GitWorkspaceResult>(result.Payload.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            }
            throw new InvalidOperationException("No response");
        }
        var one = await Prepare(first);
        f.Db.SourceControlPublications.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, RepositoryId = one.RepositoryId,
            WorkspaceId = one.WorkspaceId, CommitSha = new string('b', 40), Status = SourceControlPublicationStatus.Merged });
        await f.Db.SaveChangesAsync();
        var two = await Prepare(second);
        Assert.Equal(one.RepositoryId, two.RepositoryId); Assert.NotEqual(one.WorkspaceId, two.WorkspaceId);
        Assert.Equal(first.Id, one.WorkItemId); Assert.Equal(second.Id, two.WorkItemId);
        var taskWorkspaces = await f.Db.SourceControlWorkspaces.Where(x => x.WorkItemId == first.Id || x.WorkItemId == second.Id).ToListAsync();
        Assert.Equal(2, taskWorkspaces.Select(x => x.BranchName).Distinct().Count());
        Assert.Null(host.Requests.Last().ExpectedCommitSha);
        Assert.Equal(one.WorkspaceId, (await Prepare(first)).WorkspaceId);
        Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-business")]
    [InlineData("different-owner")]
    [InlineData("different-installation")]
    [InlineData("archived")]
    [InlineData("unfinished")]
    [InlineData("unpublished")]
    public async Task Follow_up_reuses_authorized_project_and_pins_published_source(string scenario)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        const string capability = "source-control.personal-work.prepare.v1";
        var actions = new HashSet<string> { capability, GitWorkspaceCapabilities.Prepare };
        (await f.Db.AgentInstallationGrants.SingleAsync()).RequiredCapabilitiesJson = JsonSerializer.Serialize(actions);
        var actor = await f.Db.CoreOrganizationUsers.SingleAsync();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            Kind = WorkBoardKind.Personal, OwnerOrganizationUserId = actor.Id };
        WorkTask Ticket(string title) => new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            BoardId = board.Id, Board = board, Title = title, Status = WorkTaskStatus.Running,
            ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            SourceConversationId = Guid.NewGuid(), SourceMessageId = Guid.NewGuid() };
        var original = Ticket("Breakout");
        f.Db.WorkBoards.Add(board); f.Db.CoreWorkTasks.Add(original); await f.Db.SaveChangesAsync();
        var internalHost = DispatchProxy.Create<ITrustedSourceControlHostClient, SourceHost>();
        using var http = new HttpClient(new CoreTransport(new PersonalRepositoryBroker(f.Db, internalHost))) { BaseAddress = new Uri("http://core/") };
        var host = new Host(new CoreWorkspaceBrokerClient(http));
        var handler = new GitWorkspaceCapabilityHandler(f.Db, host, null!, null!, WorkspaceSyncTestOptions.Value);
        var session = new AgentSession("session", "developer", f.Installation.ToString(), f.Organization.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), actions, 1));
        async Task<CapabilityResult> Prepare(WorkTask task, Guid? sourceId = null)
        {
            var request = new RequestCapability { RequestId = task.Id.ToString(), Capability = capability, ContentType = "application/json",
                Payload = JsonPayload.From(new PreparePersonalGitWorkspaceRequest(task.Id, $"prepare:{task.Id:N}") { SourceWorkItemId = sourceId }) };
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
            return Assert.Single(results);
        }
        Assert.True((await Prepare(original)).Succeeded);
        var repo = Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
        var originalWorkspace = Assert.Single(await f.Db.SourceControlWorkspaces.ToListAsync());
        original.Status = WorkTaskStatus.Completed;
        if (scenario != "unpublished") f.Db.SourceControlPublications.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RepositoryId = repo.Id, WorkspaceId = originalWorkspace.Id, CommitSha = new string('b', 40), CreatedAt = DateTimeOffset.UtcNow });
        if (scenario == "foreign-business") original.OrganizationId = Guid.NewGuid();
        if (scenario == "different-owner") original.BoardId = Guid.NewGuid();
        if (scenario == "different-installation") original.AssignedAgentInstallationId = Guid.NewGuid();
        if (scenario == "archived") original.ArchivedAt = DateTimeOffset.UtcNow;
        if (scenario == "unfinished") original.Status = WorkTaskStatus.Running;
        var fix = Ticket("Fix bricks"); f.Db.CoreWorkTasks.Add(fix); await f.Db.SaveChangesAsync();
        var result = await Prepare(fix, original.Id);
        if (scenario != "valid")
        {
            Assert.False(result.Succeeded);
            Assert.Equal(0, fix.AssignmentRevision);
            Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
            Assert.Equal(1, host.Prepares);
            return;
        }
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, host.Prepares);
        Assert.Equal(repo.Id, host.Requests[1].RepositoryId);
        Assert.Equal(new string('b', 40), host.Requests[1].ExpectedCommitSha);
        Assert.NotEqual(host.Requests[0].DeterministicBranch, host.Requests[1].DeterministicBranch);
        Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
        var binding = fix.DevelopmentBriefJson;
        // Retry retains the project and source even when a newer publication appears.
        f.Db.SourceControlPublications.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RepositoryId = repo.Id, WorkspaceId = originalWorkspace.Id, CommitSha = new string('c', 40), CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        await f.Db.SaveChangesAsync();
        Assert.True((await Prepare(fix, original.Id)).Succeeded);
        Assert.Equal(binding, fix.DevelopmentBriefJson);
        Assert.Equal(2, host.Prepares);
        Assert.False((await Prepare(fix)).Succeeded); // A retry cannot silently turn into a new project.
        // A further follow-up follows the stable project identity, not the intermediate ticket ID.
        fix.Status = WorkTaskStatus.Completed;
        var next = Ticket("Improve controls"); f.Db.CoreWorkTasks.Add(next); await f.Db.SaveChangesAsync();
        Assert.True((await Prepare(next, fix.Id)).Succeeded);
        Assert.Single(await f.Db.SourceControlRepositories.ToListAsync());
        Assert.Equal(repo.Id, host.Requests[2].RepositoryId);
        Assert.Equal(new string('c', 40), host.Requests[2].ExpectedCommitSha);
        var unrelated = Ticket("New racing game"); f.Db.CoreWorkTasks.Add(unrelated); await f.Db.SaveChangesAsync();
        Assert.True((await Prepare(unrelated)).Succeeded);
        Assert.Equal(2, await f.Db.SourceControlRepositories.CountAsync());
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
        public List<TrustedWorkspacePrepareRequest> Requests { get; } = [];
        public Task CreatePersonalRepositoryAsync(AgentBrokerPersonalRepositoryRequest r, CancellationToken ct) => core.CreatePersonalRepositoryAsync(r, ct);
        public Task<TrustedWorkspaceMaterialization> PrepareAsync(TrustedWorkspacePrepareRequest r, CancellationToken ct)
        { Prepares++; Requests.Add(r); return Task.FromResult(new TrustedWorkspaceMaterialization("personal-" + r.WorkItemId.ToString("N"), $"/workspace/{r.WorkItemId:N}/1", r.ExpectedCommitSha ?? new string('a', 40), false)); }
        public Task<TrustedWorkspaceRefresh> RefreshAsync(TrustedWorkspaceOperationRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitWorkspaceInspection> InspectAsync(TrustedWorkspaceOperationRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<TrustedWorkspacePublication> PublishAsync(TrustedWorkspacePublishRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitWorkspaceCleanupResult> CleanupAsync(TrustedWorkspaceCleanupRequest r, CancellationToken ct) => throw new NotSupportedException();
    }
}
