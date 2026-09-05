using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Security;
using CSweet.Application.SourceControl;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class InternalGitProvisioningTests
{
    [Fact]
    public async Task AnyGrantedAgentCanRequestAnInternalRepositoryAndWorkerAssignsItsTeam()
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        Assert.True((await InvokeAsync(db, fixture, auth)).Succeeded);
        var request = await db.RepositoryProvisioningRequests.SingleAsync(); Assert.Equal(RepositoryProvisioningStatus.Pending, request.Status);
        Assert.True((await InvokeAsync(db, fixture, auth)).Succeeded); Assert.Single(db.RepositoryProvisioningRequests);
        var host = new Host();
        Assert.True(await Processor(db, host, auth).TryProcessNextAsync());
        var repository = await db.SourceControlRepositories.SingleAsync();
        Assert.Equal(request.Id, repository.Id); Assert.Equal(SourceControlRepositoryStatus.Ready, repository.Status);
        Assert.True((await db.TeamRepositoryPolicies.SingleAsync()).IsPrimary);
        Assert.Equal(fixture.Team, (await db.TeamRepositoryPolicies.SingleAsync()).TeamId);
        Assert.Equal(RepositoryProvisioningStatus.Completed, request.Status); Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task LostStorageResponseResumesWithTheSameRepositoryIdentity()
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        await InvokeAsync(db, fixture, auth); var host = new Host { Fail = true };
        await Processor(db, host, auth).TryProcessNextAsync();
        var request = await db.RepositoryProvisioningRequests.SingleAsync(); var id = request.RepositoryId;
        Assert.Equal(RepositoryProvisioningStatus.Provisioning, request.Status);
        request.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3); await db.SaveChangesAsync(); host.Fail = false;
        await Processor(db, host, auth).TryProcessNextAsync();
        Assert.Equal(id, request.RepositoryId); Assert.Single(db.SourceControlRepositories); Assert.Equal(2, host.Calls);
        Assert.Equal(RepositoryProvisioningStatus.Completed, request.Status);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("policy")]
    public async Task WorkerRevalidatesAuthorityBeforeCreatingRepository(string revoked)
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        await InvokeAsync(db, fixture, auth);
        if (revoked == "grant") auth.Allowed = false;
        if (revoked == "membership") (await db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        if (revoked == "policy") (await db.RepositoryProvisioningPolicies.SingleAsync()).Revision++;
        await db.SaveChangesAsync(); var host = new Host();
        await Processor(db, host, auth).TryProcessNextAsync();
        Assert.Equal(0, host.Calls); Assert.Empty(db.SourceControlRepositories);
        Assert.Equal(RepositoryProvisioningStatus.Failed, (await db.RepositoryProvisioningRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task QuotaCountsQueuedReservationsAndApprovalPolicyDelaysCreation()
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        await InternalGitProvisioningDefaults.EnsureAsync(db, fixture.Business, default);
        var policy = await db.RepositoryProvisioningPolicies.SingleAsync(); policy.MaximumRepositories = 1; policy.RequiresManagerApproval = true;
        await db.SaveChangesAsync();
        await InvokeAsync(db, fixture, auth);
        Assert.Equal(RepositoryProvisioningStatus.AwaitingApproval, (await db.RepositoryProvisioningRequests.SingleAsync()).Status);
        Assert.Single(db.SourceControlApprovals);
        await InvokeAsync(db, fixture, auth, "second-key"); Assert.Single(db.RepositoryProvisioningRequests);
        Assert.False(await Processor(db, new Host(), auth).TryProcessNextAsync());
    }

    [Fact]
    public async Task DeniedGrantCannotCreateDefaultsOrQueueRequest()
    {
        await using var db = Database(); var fixture = await SeedAsync(db);
        Assert.False((await InvokeAsync(db, fixture, new Authorization { Allowed = false })).Succeeded);
        Assert.Empty(db.SourceControlConnections); Assert.Empty(db.RepositoryProvisioningRequests);
    }

    [Fact]
    public async Task ExplicitGitHubTemplateKeepsItsProviderAndCannotReplayAsInternal()
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = fixture.Business, Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ManagedGitHub, Status = SourceControlConnectionStatus.Connected, AccountLogin = "company", AccountType = "Organization", ProvisionerInstallationId = 42, SourceAccessInstallationId = 41 };
        var template = new SourceControlRepositoryTemplate { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ConnectionId = connection.Id, Name = "approved" };
        db.AddRange(connection, template, new RepositoryProvisioningPolicy { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ConnectionId = connection.Id,
            MaximumRepositories = 10, ApprovedTemplatesJson = JsonSerializer.Serialize(new[] { template.Id }) });
        await db.SaveChangesAsync();
        Assert.True((await InvokeAsync(db, fixture, auth, template: template.Id)).Succeeded);
        Assert.Equal(connection.Id, (await db.RepositoryProvisioningRequests.SingleAsync()).ConnectionId);
        Assert.False((await InvokeAsync(db, fixture, auth)).Succeeded);
        Assert.Single(db.SourceControlConnections);
        Assert.False((await InvokeAsync(db, fixture, auth, "foreign-template", Guid.NewGuid())).Succeeded);
    }

    [Fact]
    public async Task BusinessDefaultRoutesNewRequestsAndReplayKeepsOriginalProvider()
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = fixture.Business, Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ManagedGitHub, Status = SourceControlConnectionStatus.Connected, AccountLogin = "company", AccountType = "Organization", ProvisionerInstallationId = 42, SourceAccessInstallationId = 41 };
        var template = new SourceControlRepositoryTemplate { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ConnectionId = connection.Id, Name = "approved" };
        db.AddRange(connection, template, new RepositoryProvisioningPolicy { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ConnectionId = connection.Id,
            MaximumRepositories = 10, RequiresManagerApproval = false, ApprovedTemplatesJson = JsonSerializer.Serialize(new[] { template.Id }) });
        var settings = new SourceControlBusinessSettings { OrganizationId = fixture.Business, DefaultTemplateId = template.Id };
        db.Add(settings); await db.SaveChangesAsync();
        Assert.True((await InvokeAsync(db, fixture, auth)).Succeeded);
        var queued = await db.RepositoryProvisioningRequests.SingleAsync(); Assert.Equal(connection.Id, queued.ConnectionId); Assert.True(queued.UsedBusinessDefault);
        Assert.Equal(fixture.Team, queued.TeamId);
        settings.DefaultTemplateId = null; settings.Revision++; await db.SaveChangesAsync();
        Assert.True((await InvokeAsync(db, fixture, auth)).Succeeded); // Same key stays on GitHub.
        Assert.Single(db.RepositoryProvisioningRequests);
        Assert.False((await InvokeAsync(db, fixture, auth, template: template.Id)).Succeeded); // Explicit/default cannot impersonate one another.
        Assert.True((await InvokeAsync(db, fixture, auth, "next-request")).Succeeded);
        Assert.Equal(2, await db.RepositoryProvisioningRequests.CountAsync());
        Assert.Equal(1, await db.RepositoryProvisioningRequests.CountAsync(r => r.ConnectionId == connection.Id));
        settings.DefaultTemplateId = template.Id; connection.Status = SourceControlConnectionStatus.Disconnected; await db.SaveChangesAsync();
        await InvokeAsync(db, fixture, auth, "unavailable");
        Assert.Equal(2, await db.RepositoryProvisioningRequests.CountAsync()); // Never silently substitutes internal Git.
    }

    [Fact]
    public async Task GitHubWorkerRechecksAgentGrantBeforeCallingExternalProvider()
    {
        await using var db = Database(); var fixture = await SeedAsync(db); var auth = new Authorization();
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = fixture.Business, Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ManagedGitHub, Status = SourceControlConnectionStatus.Connected, AccountLogin = "company", AccountType = "Organization", ProvisionerInstallationId = 42, SourceAccessInstallationId = 41 };
        var template = new SourceControlRepositoryTemplate { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ConnectionId = connection.Id, Name = "approved" };
        db.AddRange(connection, template, new RepositoryProvisioningPolicy { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ConnectionId = connection.Id,
            MaximumRepositories = 10, RequiresManagerApproval = false, ApprovedTemplatesJson = JsonSerializer.Serialize(new[] { template.Id }) });
        await db.SaveChangesAsync();
        Assert.True((await InvokeAsync(db, fixture, auth, template: template.Id)).Succeeded);
        auth.Allowed = false;
        Assert.True(await Processor(db, new Host(), auth).TryProcessNextAsync());
        var queued = await db.RepositoryProvisioningRequests.SingleAsync();
        Assert.Equal(RepositoryProvisioningStatus.Failed, queued.Status); Assert.Equal("grant_revoked", queued.FailureCode);
    }

    private static RepositoryProvisioningProcessor Processor(CSweetDbContext db, Host host, Authorization auth) => new(db, new UnavailableTrustedProvisioningHostClient(), TimeProvider.System, host, auth);
    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed record Fixture(Guid Business, Guid Agent, Guid Team, Guid Workstream);
    private static async Task<Fixture> SeedAsync(CSweetDbContext db)
    {
        var fixture = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.Business, AgentInstallationId = fixture.Agent, DisplayName = "Developer", IsActive = true };
        db.CoreOrganizationUsers.Add(employee);
        db.AgentInstallations.Add(new() { Id = fixture.Agent, BusinessId = fixture.Business.ToString("D"), IsEnabled = true });
        db.OrganizationTeams.Add(new() { Id = fixture.Team, OrganizationId = fixture.Business, Name = "Delivery" });
        db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Business, TeamId = fixture.Team, OrganizationUserId = employee.Id });
        db.Workstreams.Add(new() { Id = fixture.Workstream, OrganizationId = fixture.Business, Name = "Product" });
        await db.SaveChangesAsync(); return fixture;
    }
    private static async Task<CapabilityResult> InvokeAsync(CSweetDbContext db, Fixture fixture, Authorization auth, string key = "create-one", Guid? template = null)
    {
        var session = new AgentSession("session", "com.example.any-developer", fixture.Agent.ToString(), fixture.Business.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { SourceControlCapabilities.ProvisionRepository }, 1));
        var request = new RequestCapability { RequestId = "request", Capability = SourceControlCapabilities.ProvisionRepository,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new ProvisionSourceControlRepositoryRequest(fixture.Workstream, "Product", null, template ?? Guid.Empty, key), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        var handler = new GitWorkspaceCapabilityHandler(db, new UnavailableTrustedGitHostClient(), auth, new Signer());
        var results = new List<CapabilityResult>(); await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        return Assert.Single(results);
    }
    private sealed class Signer : ISourceControlDecisionSigner
    {
        public string Sign(SourceControlMergeDecision decision) => throw new NotSupportedException();
        public bool Verify(SourceControlMergeDecision decision, string signature) => throw new NotSupportedException();
    }
    private sealed class Authorization : IScopedActionAuthorizationService
    {
        public bool Allowed { get; set; } = true;
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid organizationId, GrantSubjectKind subjectKind, Guid subjectId, string action, GrantScopeKind resourceScopeKind, Guid? resourceScopeId, CancellationToken cancellationToken = default) => Task.FromResult(new ScopedAuthorizationDecision(Allowed, action));
    }
    private sealed class Host : ITrustedSourceControlHostClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<InternalGitRepositoryInspection> ExecuteInternalAsync(InternalGitRepositoryRequest request, CancellationToken cancellationToken = default)
        { Calls++; if (Fail) throw new HttpRequestException(); return Task.FromResult(new InternalGitRepositoryInspection("main", [], [], [])); }
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
