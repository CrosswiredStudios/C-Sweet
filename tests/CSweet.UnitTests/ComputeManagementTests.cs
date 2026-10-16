using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Compute;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.UnitTests;

public sealed class ComputeManagementTests
{
    private static async Task<Guid> OwnerAsync(ComputeBrokerTests.Fixture fixture)
    {
        var user = Guid.NewGuid();
        fixture.Db.CoreOrganizationUsers.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = fixture.Organization, ApplicationUserId = user,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        return user;
    }

    private static ComputeDefaultsService Defaults(ComputeBrokerTests.Fixture fixture, int lifetime = 600) => new(fixture.Db,
        new ComputeBrokerTests.Clock(), new(fixture.Db, new ComputeBrokerTests.Clock(), new AuditExecutionContextAccessor()),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CSweet:Compute:Defaults:MaximumLifetimeSeconds"] = lifetime.ToString()
        }).Build());

    private static async Task ReadyAsync(ComputeBrokerTests.Fixture fixture)
    {
        (await fixture.Db.Workstreams.SingleAsync()).Status = WorkstreamStatus.Active;
        fixture.Db.Add(new ComputeLocalSetup
        {
            Id = Guid.NewGuid(), OrganizationId = fixture.Organization, State = "Ready", TemplateId = "ubuntu-clean"
        });
        fixture.Db.ComputeTemplates.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = fixture.Organization, TemplateId = "ubuntu-clean", Enabled = true,
            TemplateJson = JsonSerializer.Serialize(new ComputeTemplate("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), ["docker"]), ComputeProtocol.Json)
        });
        await fixture.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task OwnerSetupIsIdempotentAndAuditedWithoutRequiringAnAgent()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var user = await OwnerAsync(fixture);
        await ComputeManagementEndpoints.StartSetupAsync(fixture.Db, fixture.Organization, user, default);
        await ComputeManagementEndpoints.StartSetupAsync(fixture.Db, fixture.Organization, user, default);
        Assert.Equal("Pending", (await fixture.Db.Set<ComputeLocalSetup>().SingleAsync()).State);
        Assert.Single(await fixture.Db.AuditOutbox.Where(entry => entry.RequestJson.Contains("compute.workspace.preparation-requested.v1")).ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeManagementEndpoints.StartSetupAsync(fixture.Db, Guid.NewGuid(), user, default));
        var owner = await fixture.Db.CoreOrganizationUsers.SingleAsync(member => member.ApplicationUserId == user);
        owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeManagementEndpoints.StartSetupAsync(fixture.Db, fixture.Organization, user, default));
    }

    [Fact]
    public async Task OwnerCreationUsesLinuxPermissionsAndReplaysOneInstance()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var user = await OwnerAsync(fixture); await ReadyAsync(fixture);
        var defaults = Defaults(fixture);
        var option = Assert.Single(await defaults.ListCreationOptionsAsync(fixture.Organization, default));
        Assert.Equal(fixture.Workstream, option.ProjectId);
        var request = new CreateLinuxInstance(fixture.Installation, fixture.Workstream, "linux-build-01", Guid.NewGuid(), 600);
        var first = await ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker, fixture.Organization, user, request, default);
        var replay = await ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker, fixture.Organization, user, request, default);
        Assert.Equal(first.Id, replay.Id);
        var environment = await fixture.Db.ComputeEnvironments.SingleAsync();
        var specification = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!;
        Assert.Equal("linux", specification.OperatingSystem);
        Assert.Equal(ComputeNetworkMode.None, specification.NetworkPolicy.Mode);
        Assert.Equal(new ComputeResources(2, 2048, 20480), specification.Resources);
        Assert.Single(await fixture.Db.ComputeOperations.ToListAsync());
        Assert.Contains(await fixture.Db.AuditOutbox.ToListAsync(), entry => entry.RequestJson.Contains(user.ToString()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults,
            fixture.Broker, fixture.Organization, user, request with { Name = "changed-name" }, default));
    }

    [Fact]
    public async Task CreationRejectsWrongOwnerProjectAndLifetimeBeforeProvisioning()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var user = await OwnerAsync(fixture); await ReadyAsync(fixture);
        var defaults = Defaults(fixture);
        var request = new CreateLinuxInstance(fixture.Installation, fixture.Workstream, "linux-build-01", Guid.NewGuid(), 600);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker,
            fixture.Organization, Guid.NewGuid(), request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker,
            fixture.Organization, user, request with { ProjectId = Guid.NewGuid() }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker,
            fixture.Organization, user, request with { LifetimeSeconds = 0 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker,
            fixture.Organization, user, request with { Name = "../unsafe" }, default));
        Assert.Empty(await fixture.Db.ComputeEnvironments.ToListAsync());
    }

    [Fact]
    public async Task RevokedProvisionGrantIsNotRevivedByManualCreation()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var user = await OwnerAsync(fixture); await ReadyAsync(fixture);
        var defaults = Defaults(fixture);
        await defaults.ReadProjectAsync(fixture.Organization, fixture.Installation, fixture.Workstream, default);
        foreach (var grant in await fixture.Db.ScopedActionGrants.Where(grant => grant.Action == InfrastructureActions.Provision).ToListAsync())
            grant.RevokedAt = new ComputeBrokerTests.Clock().GetUtcNow().AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeManagementEndpoints.CreateAsync(fixture.Db, defaults, fixture.Broker,
            fixture.Organization, user, new(fixture.Installation, fixture.Workstream, "linux-build-01", Guid.NewGuid(), 600), default));
        Assert.Empty(await fixture.Db.ComputeEnvironments.ToListAsync());
    }
}
