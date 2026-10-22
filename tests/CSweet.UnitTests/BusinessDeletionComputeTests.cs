using CSweet.Application.Compute;
using CSweet.Api.Core;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class BusinessDeletionComputeTests
{
    [Theory]
    [InlineData(OrganizationStatus.Deleting)]
    [InlineData(OrganizationStatus.DeletionFailed)]
    public async Task Deletion_authorizes_destroy_without_employee_or_grants_and_survives_restart(OrganizationStatus status)
    {
        await using var f = new ComputeDispatchTests.Fixture();
        await f.InitializeAsync();
        var db = f.Broker.Db;
        var environment = await db.ComputeEnvironments.SingleAsync();
        await RemoveEmployeeAuthorityAsync(db);
        (await db.CoreOrganizations.SingleAsync()).Status = status;
        // Cleanup must work after the resource lease expires, too.
        environment.LeaseExpiresAt = f.Time.Now.AddHours(-1);
        await db.SaveChangesAsync();

        var request = new ChangeComputeLifecycle(environment.Id, environment.Generation, InfrastructureActions.Destroy, "owner-deletion");
        var first = await f.Broker.Broker.ChangeLifecycleAsync(f.Broker.Organization, f.Broker.Installation, request, default);
        var replay = await f.Broker.Broker.ChangeLifecycleAsync(f.Broker.Organization, f.Broker.Installation, request, default);
        Assert.Equal(first.Generation, replay.Generation);
        Assert.Equal(ComputeDesiredState.Destroyed, first.DesiredState);
        Assert.Null(environment.TeardownConfirmedAt);
        var destroy = Assert.Single(await db.ComputeOperations.Where(x => x.Action == InfrastructureActions.Destroy).ToListAsync());
        Assert.True(await db.ComputeProviderWakes.AnyAsync(x => x.OperationId == destroy.Id));
        Assert.Contains(await db.AuditOutbox.ToListAsync(), x => x.RequestJson.Contains("business-deletion"));
        Assert.Empty(await db.ScopedActionGrants.ToListAsync());

        await using var restarted = new CSweetDbContext(f.Broker.Options);
        var packet = await f.Authorizer(restarted).ClaimAsync(destroy.Id, default);
        Assert.NotNull(packet);
        var claim = Assert.Single(f.Signing.Claims);
        Assert.Equal(InfrastructureActions.Destroy, claim.Action);
        Assert.Equal(environment.Id, claim.EnvironmentId);
        Assert.Equal(f.Broker.Organization, claim.OrganizationId);
        Assert.Equal(first.Generation, claim.Generation);
        Assert.Equal(f.Time.Now.AddMinutes(1), claim.ExpiresAt);
        Assert.Equal(claim.ExpiresAt, (await restarted.ComputeOperations.SingleAsync(x => x.Id == destroy.Id)).DispatchLeaseExpiresAt);
        Assert.Null(await f.Authorizer(restarted).ClaimAsync(destroy.Id, default));
        var authority = Assert.Single(claim.Grants);
        Assert.Equal(f.Broker.Organization, authority.GrantId);
        Assert.Equal(InfrastructureActions.Destroy, authority.Action);
        var verifier = new ComputeDispatchVerifier(
            new(f.Broker.Organization, environment.ProviderNodeId!.Value, environment.ProviderId!, await f.Signing.GetIdentityAsync(default)),
            new(environment.ProviderId!, [], [InfrastructureActions.Destroy], [], false, false),
            new(2, 4096, 20480), new Dictionary<string, ComputeTemplate>(), f.Time);
        Assert.Equal(environment.Id, verifier.Verify(packet).Authorization.EnvironmentId);
    }

    [Fact]
    public async Task Deletion_of_another_business_does_not_authorize_destroy_or_spoofed_environment()
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        var environment = await f.Send();
        await RemoveEmployeeAuthorityAsync(f.Db);
        var otherBusiness = Guid.NewGuid();
        f.Db.CoreOrganizations.Add(new() { Id = otherBusiness, Name = "Other business", Status = OrganizationStatus.Deleting });
        await f.Db.SaveChangesAsync();
        var request = new ChangeComputeLifecycle(environment.Id, environment.Generation, InfrastructureActions.Destroy, "business-delete:spoof");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation, request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Broker.ChangeLifecycleAsync(otherBusiness, f.Installation, request, default));
        Assert.DoesNotContain(await f.Db.ComputeOperations.ToListAsync(), x => x.Action == InfrastructureActions.Destroy);
    }

    [Theory]
    [InlineData(InfrastructureActions.Start)]
    [InlineData(InfrastructureActions.Restart)]
    [InlineData(InfrastructureActions.Stop)]
    public async Task Deletion_intent_does_not_grant_other_lifecycle_actions(string action)
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        var environment = await f.Send();
        await RemoveEmployeeAuthorityAsync(f.Db);
        (await f.Db.CoreOrganizations.SingleAsync()).Status = OrganizationStatus.Deleting;
        await f.Db.SaveChangesAsync();
        var error = await Record.ExceptionAsync(() => f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(environment.Id, environment.Generation, action, "business-delete:other-action"), default));
        if (action == InfrastructureActions.Stop) Assert.IsType<UnauthorizedAccessException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Single(await f.Db.ComputeOperations.ToListAsync());
    }

    [Fact]
    public async Task Dispatch_rechecks_current_business_deletion_intent()
    {
        await using var f = new ComputeDispatchTests.Fixture();
        await f.InitializeAsync();
        var db = f.Broker.Db;
        (await db.CoreOrganizations.SingleAsync()).Status = OrganizationStatus.Deleting;
        await db.SaveChangesAsync();
        var environment = await db.ComputeEnvironments.SingleAsync();
        await f.Broker.Broker.ChangeLifecycleAsync(f.Broker.Organization, f.Broker.Installation,
            new(environment.Id, environment.Generation, InfrastructureActions.Destroy, "owner-deletion"), default);
        var destroy = await db.ComputeOperations.SingleAsync(x => x.Action == InfrastructureActions.Destroy);
        // A recorded receipt alone cannot authorize teardown without current intent.
        (await db.CoreOrganizations.SingleAsync()).Status = OrganizationStatus.Active;
        await db.SaveChangesAsync();
        Assert.Null(await f.Authorizer().ClaimAsync(destroy.Id, default));
        Assert.Equal("dispatch-authority-denied", destroy.FailureCode);
        Assert.Empty(f.Signing.Claims);
    }

    internal static async Task RemoveEmployeeAuthorityAsync(CSweetDbContext db)
    {
        var installation = await db.AgentInstallations.Include(x => x.Grant).SingleAsync();
        installation.IsEnabled = false;
        installation.Grant!.RequiredCapabilitiesJson = "[]";
        (await db.CoreOrganizationUsers.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow;
        db.ScopedActionGrants.RemoveRange(await db.ScopedActionGrants.ToListAsync());
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("owner", true)]
    [InlineData("manager", false)]
    [InlineData("archived", false)]
    [InlineData("inactive", false)]
    [InlineData("agent", false)]
    [InlineData("other-business", false)]
    [InlineData("other-user", false)]
    public async Task Only_current_human_owner_can_persist_deletion_intent(string scenario, bool allowed)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var business = Guid.NewGuid();
        var user = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = scenario == "other-business" ? Guid.NewGuid() : business,
            ApplicationUserId = scenario == "other-user" ? Guid.NewGuid() : user,
            EmployeeType = scenario == "agent" ? EmployeeType.Agent : EmployeeType.Human,
            PermissionLevel = scenario == "manager" ? OrganizationPermissionLevel.Manager : OrganizationPermissionLevel.Owner,
            IsActive = scenario != "inactive", ArchivedAt = scenario == "archived" ? DateTimeOffset.UtcNow : null
        });
        await db.SaveChangesAsync();
        Assert.Equal(allowed, await CoreOrganizationEndpoints.IsDeletionOwnerAsync(db, business, user, default));
    }
}
