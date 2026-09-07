using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ConnectorBindingSelectionTests
{
    [Fact]
    public async Task ChoicesContainOnlyReviewedSameOrganizationAccountsAndNeverAutoSelect()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        f.Db.AgentCapabilityBindings.RemoveRange(await f.Db.AgentCapabilityBindings.ToArrayAsync());
        f.Connection.ExternalAccountName = "Example business account";
        await f.Db.SaveChangesAsync();
        var service = new ConnectorBindingService(f.Db);
        var dependency = Assert.Single(await service.GetChoicesAsync(f.Organization, f.Requester.Id, default));
        Assert.Null(dependency.SelectedInstallationId);
        Assert.Equal("com.example.connector", dependency.PluginId);
        var choice = Assert.Single(dependency.Choices);
        Assert.True(choice.CanSelect);
        Assert.Equal("Example business account", Assert.Single(choice.Accounts).Name);
        Assert.Empty(await f.Db.AgentCapabilityBindings.ToArrayAsync());
        await service.BindAsync(f.Organization, f.Requester.Id, "account", choice.InstallationId, default);
        Assert.Equal(choice.InstallationId, Assert.Single(await service.GetChoicesAsync(f.Organization, f.Requester.Id, default)).SelectedInstallationId);
        await service.BindAsync(f.Organization, f.Requester.Id, "account", choice.InstallationId, default);
        Assert.Single(await f.Db.AgentCapabilityBindings.ToArrayAsync());
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("unconfirmed")]
    [InlineData("needs-setup")]
    [InlineData("consumer-grant")]
    [InlineData("provider-grant")]
    [InlineData("profile")]
    public async Task IncompleteAuthorityIsVisibleButCannotBeSelected(string change)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        switch (change)
        {
            case "disconnected": f.Connection.Status = PluginConnectionStatus.Revoked; break;
            case "unconfirmed": f.Connection.BoundResourceId = null; break;
            case "needs-setup": f.Connector.SetupState = PluginSetupState.NeedsSetup; break;
            case "consumer-grant": f.Requester.Grant!.RequiredCapabilitiesJson = "[]"; break;
            case "provider-grant": f.Connector.Grant!.ProvidedCapabilitiesJson = "[]"; break;
            case "profile": (await f.Db.ConnectorProfileApprovals.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow; break;
        }
        await f.Db.SaveChangesAsync();
        var service = new ConnectorBindingService(f.Db);
        var choice = Assert.Single(Assert.Single(await service.GetChoicesAsync(f.Organization, f.Requester.Id, default)).Choices);
        Assert.False(choice.CanSelect);
        Assert.NotNull(choice.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync(f.Organization, f.Requester.Id, "account", f.Connector.Id, default));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("publisher")]
    [InlineData("version")]
    [InlineData("disabled")]
    public async Task IncompatibleOrOtherTenantInstallationsAreNotDisclosed(string change)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        switch (change)
        {
            case "tenant": f.Connector.BusinessId = Guid.NewGuid().ToString("D"); break;
            case "publisher": f.Connector.PackageVersion!.PublisherId = "com.impostor"; break;
            case "version": f.Connector.PackageVersion!.Version = "0.2.0"; break;
            case "disabled": f.Connector.IsEnabled = false; break;
        }
        await f.Db.SaveChangesAsync();
        var service = new ConnectorBindingService(f.Db);
        Assert.Empty(Assert.Single(await service.GetChoicesAsync(f.Organization, f.Requester.Id, default)).Choices);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetChoicesAsync(Guid.NewGuid(), f.Requester.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync(f.Organization, f.Requester.Id, "account", f.Connector.Id, default));
    }

    [Fact]
    public async Task ReapprovingChangedBuildRevokesAutonomyAndInvalidatesPreparedPlan()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var plan = await f.Prepare("one");
        var policy = new PluginStandingPolicy { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            AgentInstallationId = f.Requester.Id, Status = PluginStandingPolicyStatus.Approved };
        f.Db.PluginStandingPolicies.Add(policy);
        f.Connector.PackageVersion!.PackageDigest = new string('b', 64);
        (await f.Db.ConnectorProfileApprovals.SingleAsync()).PackageDigest = f.Connector.PackageVersion.PackageDigest;
        await f.Db.SaveChangesAsync();
        await new ConnectorBindingService(f.Db).BindAsync(f.Organization, f.Requester.Id, "account", f.Connector.Id, default);
        Assert.Equal(PluginStandingPolicyStatus.Revoked, policy.Status);
        Assert.NotNull(policy.RevokedAt);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RevalidateAsync(f.Organization, f.Requester.Id, plan.Id, plan.PlanHash, default));
    }
}
