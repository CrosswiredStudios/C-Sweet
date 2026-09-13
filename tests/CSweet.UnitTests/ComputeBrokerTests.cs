using CSweet.Compute.Contracts;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeBrokerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    internal sealed class Catalog : IComputeTemplateCatalog
    {
        public bool Enabled { get; set; } = true;
        public Task<RegisteredComputeTemplate?> ResolveAsync(Guid organizationId, string templateId, CancellationToken token) =>
            Task.FromResult<RegisteredComputeTemplate?>(Enabled ? new(new("ubuntu-clean", "linux", "x64",
                "sha256:" + new string('a', 64), []), "test-provider", Guid.Parse("f78f34e8-1b0f-428a-ad24-4c31d5a87781")) : null);
    }

    internal sealed class Audit : IAuditEventWriter
    {
        public List<AuditEventWriteRequest> Events { get; } = [];
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary, string? metadataJson = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add(request); return Task.FromResult(request.EventId ?? Guid.NewGuid());
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public DbContextOptions<CSweetDbContext> Options { get; }
        public CSweetDbContext Db { get; }
        public Guid Organization { get; } = Guid.NewGuid();
        public Guid Installation { get; } = Guid.NewGuid();
        public Guid Workstream { get; } = Guid.NewGuid();
        public Catalog Templates { get; } = new();
        public Audit Ledger { get; } = new();
        public ComputeBroker Broker { get; }
        public RequestComputeEnvironment Request => new(Workstream, "installer-test", "request-1",
            new("linux", "x64", "ubuntu-clean", new(2, 4096, 20480), 600));
        public Fixture(DbContextOptions<CSweetDbContext>? options = null)
        {
            Options = options ?? new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new(Options); Broker = new(Db, Templates, new Clock(), Ledger);
        }
        public async Task SeedAsync()
        {
            var actorId = Guid.NewGuid();
            Db.CoreOrganizations.Add(new() { Id = Organization, Name = "Compute tests" });
            Db.AgentInstallations.Add(new()
            {
                PackageVersion = new() { Id = Guid.NewGuid(), PackageSource = new() { Id = Guid.NewGuid(), RepositoryUrl = "https://example.invalid/compute-test" } },
                Id = Installation, InstallationKey = Guid.NewGuid(), BusinessId = Organization.ToString("D"), IsEnabled = true,
                RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready,
                Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = Installation,
                    RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { InfrastructureActions.Provision, InfrastructureActions.Read, InfrastructureActions.List, InfrastructureActions.Start, InfrastructureActions.Stop, InfrastructureActions.Restart, InfrastructureActions.Destroy }) }
            });
            Db.CoreOrganizationUsers.Add(new() { Id = actorId, OrganizationId = Organization,
                AgentInstallationId = Installation, IsActive = true, EmployeeType = EmployeeType.Agent });
            Db.Workstreams.Add(new() { Id = Workstream, OrganizationId = Organization, AccountableManagerOrganizationUserId = actorId });
            var constraints = new ComputeGrantConstraints(1, new(2, 4096, 20480), 1, 600,
                ["linux"], ["x64"], ["ubuntu-clean"]);
            foreach (var action in new[] { InfrastructureActions.Provision, InfrastructureActions.Read, InfrastructureActions.List, InfrastructureActions.Start, InfrastructureActions.Stop, InfrastructureActions.Restart, InfrastructureActions.Destroy })
                Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = Organization,
                    SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = Installation,
                    ScopeKind = GrantScopeKind.Workstream, ScopeId = Workstream, Action = action,
                    GrantedAt = Now.AddDays(-1), ExpiresAt = Now.AddHours(1), ConstraintsJson = JsonSerializer.Serialize(constraints, ComputeBroker.Json) });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }
        public Task<ComputeEnvironmentView> Send(RequestComputeEnvironment? request = null) => Broker.RequestAsync(Organization, Installation, request ?? Request, default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Replays_and_distinct_trigger_keys_share_one_environment_operation_and_wake()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var first = await f.Send();
        Assert.Equal(first.Id, (await f.Send()).Id);
        Assert.Equal(first.Id, (await f.Send(f.Request with { IdempotencyKey = "heartbeat-trigger" })).Id);
        Assert.Single(await f.Db.ComputeEnvironments.ToListAsync());
        Assert.Single(await f.Db.ComputeOperations.ToListAsync());
        var providerWake = Assert.Single(await f.Db.ComputeProviderWakes.ToListAsync());
        var placed = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(placed.OrganizationId, providerWake.OrganizationId);
        Assert.Equal(placed.ProviderNodeId, providerWake.NodeId); Assert.Equal(placed.ProviderId, providerWake.ProviderId);
        Assert.Equal((await f.Db.ComputeOperations.SingleAsync()).Id, providerWake.OperationId);
        Assert.Null(providerWake.PublishedAt);
        Assert.Equal(2, await f.Db.ComputeRequestReceipts.CountAsync());
        var wake = Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == "com.csweet.compute.changed.v1").ToListAsync());
        Assert.Equal(f.Installation, wake.TargetInstallationId);
        Assert.DoesNotContain("Provider", wake.DataJson);
        var operation = await f.Db.ComputeOperations.SingleAsync();
        Assert.Equal(f.Organization, operation.OrganizationId);
        Assert.Equal(f.Installation, operation.InstallationId);
        Assert.Contains(InfrastructureActions.Provision, operation.AuthorityJson);
        Assert.Equal(ComputeLifecycleState.Requested, first.State);
    }

    [Fact]
    public async Task Every_trigger_receipt_rejects_changed_terms_even_after_restart()
    {
        await using var f = new Fixture(); await f.SeedAsync(); await f.Send();
        await f.Send(f.Request with { IdempotencyKey = "second-trigger" });
        await using var restarted = new CSweetDbContext(f.Options);
        var broker = new ComputeBroker(restarted, f.Templates, new Clock(), f.Ledger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.RequestAsync(f.Organization, f.Installation,
            f.Request with { IdempotencyKey = "second-trigger", DesiredEnvironmentKey = "different-environment" }, default));
        Assert.Single(await f.Db.ComputeEnvironments.ToListAsync());
    }

    [Fact]
    public async Task Quota_and_current_actor_authority_are_required_before_any_admission()
    {
        await using var f = new Fixture(); await f.SeedAsync(); await f.Send();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Send(f.Request with { DesiredEnvironmentKey = "second", IdempotencyKey = "second" }));
        var grant = await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Provision);
        grant.RevokedAt = Now; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Send());
        Assert.Single(await f.Db.ComputeOperations.ToListAsync());
        var providerWake = Assert.Single(await f.Db.ComputeProviderWakes.ToListAsync());
        var placed = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(placed.OrganizationId, providerWake.OrganizationId);
        Assert.Equal(placed.ProviderNodeId, providerWake.NodeId); Assert.Equal(placed.ProviderId, providerWake.ProviderId);
        Assert.Equal((await f.Db.ComputeOperations.SingleAsync()).Id, providerWake.OperationId);
        Assert.Null(providerWake.PublishedAt);
    }

    [Fact]
    public async Task Deleted_or_cross_organization_actor_cannot_request_read_or_discover_resources()
    {
        await using var f = new Fixture(); await f.SeedAsync(); var environment = await f.Send();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ReadAsync(Guid.NewGuid(), f.Installation, environment.Id, default));
        var actor = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.Installation);
        actor.ArchivedAt = Now; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Send());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ReadAsync(f.Organization, f.Installation, environment.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ListAsync(f.Organization, f.Installation, f.Workstream, null, 10, default));
    }

    [Fact]
    public async Task Disabled_provider_catalog_does_not_prevent_authorized_terminal_state_recovery()
    {
        await using var f = new Fixture(); await f.SeedAsync(); var first = await f.Send();
        var record = await f.Db.ComputeEnvironments.SingleAsync(); record.State = ComputeLifecycleState.Destroyed;
        record.TeardownConfirmedAt = Now; await f.Db.SaveChangesAsync();
        f.Templates.Enabled = false;
        var page = await f.Broker.ListAsync(f.Organization, f.Installation, f.Workstream, null, 1, default);
        Assert.Equal(first.Id, Assert.Single(page.Items).Id);
        Assert.Equal(ComputeLifecycleState.Destroyed, (await f.Broker.ReadAsync(f.Organization, f.Installation, first.Id, default)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Send(f.Request with { IdempotencyKey = "other", DesiredEnvironmentKey = "other" }));
    }

    [Fact]
    public async Task Malformed_or_unbounded_grant_constraints_fail_closed()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var grant = await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Provision);
        grant.ConstraintsJson = "{}"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Send());
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
        Assert.Empty(await f.Db.ComputeOperations.ToListAsync());
    }
}
