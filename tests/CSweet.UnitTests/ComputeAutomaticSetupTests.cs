using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.UnitTests;

public sealed class ComputeAutomaticSetupTests
{
    [Fact]
    public async Task Legacy_default_lifetime_upgrades_once_without_overwriting_customized_grants()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CSweet:Compute:Defaults:MaximumLifetimeSeconds"] = "3600" }).Build();
        var legacy = new ComputeDefaultsService(f.Db, TimeProvider.System,
            new(f.Db, TimeProvider.System, new CSweet.Infrastructure.Setup.AuditExecutionContextAccessor()), configuration);
        await legacy.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync(); setup.State = "Ready"; setup.TemplateId = "linux-local-test";
        await legacy.ActivateAccessAsync(setup, default);
        var access = await f.Db.Set<ComputeAgentAccess>().SingleAsync();
        var grant = await f.Db.ScopedActionGrants.SingleAsync(x => x.ScopeId == access.WorkstreamId && x.Action == InfrastructureActions.Provision);
        grant.Revision = 2;
        var custom = await f.Db.ScopedActionGrants.SingleAsync(x => x.ScopeId == access.WorkstreamId && x.Action == InfrastructureActions.Read);
        custom.ConstraintsJson = custom.ConstraintsJson.Replace("\"cpuCount\":2", "\"cpuCount\":1");
        var original = custom.ConstraintsJson;
        await f.Db.SaveChangesAsync();
        await Create(f).ActivateAccessAsync(setup, default);
        await Create(f).ActivateAccessAsync(setup, default);
        Assert.Equal(3, grant.Revision);
        Assert.Equal(DateTimeOffset.MaxValue, grant.ExpiresAt);
        Assert.Equal(original, custom.ConstraintsJson);
        Assert.Equal(2, await f.Db.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.available.v1"));
        Assert.DoesNotContain(await f.Db.ScopedActionGrants.Where(x => x.ScopeId == access.WorkstreamId).Select(x => x.Action).ToListAsync(), x => x.StartsWith("network."));
    }
    [Fact]
    public async Task Approved_agent_gets_one_durable_setup_and_platform_selected_scope()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var service = Create(f);
        await service.EnsureRequestedAsync(f.Installation, default);
        await service.EnsureRequestedAsync(f.Installation, default);
        var setup = Assert.Single(await f.Db.Set<ComputeLocalSetup>().ToListAsync());
        var access = Assert.Single(await f.Db.Set<ComputeAgentAccess>().ToListAsync());
        Assert.Equal(setup.Id, access.SetupId);
        Assert.Equal("Pending", (await service.ReadAsync(f.Organization, f.Installation, default)).State);
        Assert.NotEqual(f.Workstream, access.WorkstreamId);
        Assert.Single(await f.Db.AuditOutbox.Where(x => x.SourceEntityType == null).Where(x => x.RequestJson.Contains("preparation-requested")).ToListAsync());
    }

    [Fact]
    public async Task Activation_creates_only_approved_bounded_grants_and_one_wake_without_reviving_revocation()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var service = Create(f); await service.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync(); setup.State = "Ready"; setup.TemplateId = "linux-local-test";
        await service.ActivateAccessAsync(setup, default);
        var access = await f.Db.Set<ComputeAgentAccess>().SingleAsync();
        var grant = await f.Db.ScopedActionGrants.SingleAsync(x => x.ScopeId == access.WorkstreamId && x.Action == InfrastructureActions.Provision);
        var constraints = JsonSerializer.Deserialize<CSweet.Application.Compute.ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
        Assert.Equal(1, constraints.MaximumConcurrentEnvironments); Assert.False(constraints.AllowOutbound); Assert.False(constraints.AllowPersistent);
        Assert.DoesNotContain(await f.Db.ScopedActionGrants.Where(x => x.ScopeId == access.WorkstreamId).Select(x => x.Action).ToListAsync(), x => x == InfrastructureActions.Inbound);
        grant.RevokedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        await service.ActivateAccessAsync(setup, default);
        Assert.NotNull(grant.RevokedAt);
        Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == "com.csweet.compute.available.v1").ToListAsync());
        var defaults = await service.ReadAsync(f.Organization, f.Installation, default);
        Assert.Equal("Ready", defaults.State); Assert.Equal(access.WorkstreamId, defaults.WorkstreamId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(Guid.NewGuid(), f.Installation, default));
    }

    [Fact]
    public async Task Declaring_network_capabilities_never_creates_network_grants_automatically()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var approval = await f.Db.AgentInstallationGrants.SingleAsync();
        var actions = JsonSerializer.Deserialize<List<string>>(approval.RequiredCapabilitiesJson)!;
        actions.AddRange([InfrastructureActions.Inbound, InfrastructureActions.PublishPort, InfrastructureActions.Outbound, InfrastructureActions.PrivateNetwork]);
        approval.RequiredCapabilitiesJson = JsonSerializer.Serialize(actions);
        await f.Db.SaveChangesAsync();
        var service = Create(f); await service.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync(); setup.State = "Ready"; setup.TemplateId = "linux-local-test";
        await service.ActivateAccessAsync(setup, default);
        var access = await f.Db.Set<ComputeAgentAccess>().SingleAsync();
        var granted = await f.Db.ScopedActionGrants.Where(x => x.ScopeId == access.WorkstreamId).Select(x => x.Action).ToListAsync();
        Assert.DoesNotContain(granted, x => x.StartsWith("network.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_provision_approval_does_not_prepare_compute()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var grant = await f.Db.AgentInstallationGrants.SingleAsync(); grant.RequiredCapabilitiesJson = "[\"compute.read.v1\"]";
        await f.Db.SaveChangesAsync();
        await Create(f).EnsureRequestedAsync(f.Installation, default);
        Assert.Empty(await f.Db.Set<ComputeLocalSetup>().ToListAsync());
    }

    [Fact]
    public async Task Installer_handoff_is_bound_to_one_pending_setup_and_expiration()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var setup = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = f.Organization, State = "Running",
            HandoffHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), HandoffExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        f.Db.Add(setup); await f.Db.SaveChangesAsync();
        Assert.NotNull(await ComputeLocalSetupEndpoints.AuthenticateAsync(f.Db, setup.Id, secret, default));
        Assert.Null(await ComputeLocalSetupEndpoints.AuthenticateAsync(f.Db, Guid.NewGuid(), secret, default));
        Assert.Null(await ComputeLocalSetupEndpoints.AuthenticateAsync(f.Db, setup.Id, new string('0', 64), default));
        setup.HandoffExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await f.Db.SaveChangesAsync();
        Assert.Null(await ComputeLocalSetupEndpoints.AuthenticateAsync(f.Db, setup.Id, secret, default));
    }

    private static ComputeDefaultsService Create(ComputeBrokerTests.Fixture f) => new(f.Db, TimeProvider.System,
        new ComputeGrantAdministration(f.Db, TimeProvider.System, new CSweet.Infrastructure.Setup.AuditExecutionContextAccessor()));

    [Fact]
    public async Task Docker_upgrade_waits_for_confirmed_teardown_and_blocks_old_template_defaults()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await f.Send();
        var approval = await f.Db.AgentInstallationGrants.SingleAsync();
        var actions = JsonSerializer.Deserialize<List<string>>(approval.RequiredCapabilitiesJson)!;
        actions.Add("source-control.personal-work.prepare.v1"); approval.RequiredCapabilitiesJson = JsonSerializer.Serialize(actions);
        var service = Create(f); await service.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync(); setup.State = "Ready"; setup.TemplateId = "ubuntu-clean";
        var template = new ComputeTemplateRegistration { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            TemplateId = setup.TemplateId, Enabled = true, TemplateJson = JsonSerializer.Serialize(
                new ComputeTemplate("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), ["python"]), ComputeProtocol.Json) };
        f.Db.Add(template); await f.Db.SaveChangesAsync(); await service.ActivateAccessAsync(setup, default);
        Assert.Equal("Pending", (await service.ReadAsync(f.Organization, f.Installation, default)).State);
        await ComputeLocalSetupWorker.RequestDockerUpgradeAsync(f.Db, setup, default);
        Assert.Equal("Ready", setup.State); Assert.True(template.Enabled);
        var environment = await f.Db.ComputeEnvironments.SingleAsync(); environment.TeardownConfirmedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync(); await ComputeLocalSetupWorker.RequestDockerUpgradeAsync(f.Db, setup, default);
        Assert.Equal("Pending", setup.State); Assert.False(template.Enabled);
        Assert.Null((await f.Db.Set<ComputeAgentAccess>().SingleAsync()).GrantsCreatedAt);
    }

    [Fact]
    public async Task Legacy_automatic_network_grants_are_retired_but_owner_edits_are_preserved()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var service = Create(f); await service.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync(); setup.State = "Ready"; setup.TemplateId = "ubuntu-clean";
        await service.ActivateAccessAsync(setup, default);
        var access = await f.Db.Set<ComputeAgentAccess>().SingleAsync();
        foreach (var action in new[] { InfrastructureActions.Inbound, InfrastructureActions.PublishPort })
            f.Db.ScopedActionGrants.Add(new() { Id = new(SHA256.HashData(Encoding.UTF8.GetBytes($"compute-default-grant:{access.Id:D}:{action}")).AsSpan(0, 16)),
                OrganizationId = f.Organization, SubjectId = f.Installation, ScopeId = access.WorkstreamId,
                Action = action, Revision = action == InfrastructureActions.Inbound ? 1 : 2 });
        await f.Db.SaveChangesAsync(); await service.ActivateAccessAsync(setup, default); await service.ActivateAccessAsync(setup, default);
        Assert.NotNull((await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Inbound)).RevokedAt);
        Assert.Null((await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.PublishPort)).RevokedAt);
        Assert.Single(await f.Db.AuditOutbox.Where(x => x.SourceEntityType == null).Where(x => x.RequestJson.Contains("automatic-network-grant.retired")).ToListAsync());
    }
}
