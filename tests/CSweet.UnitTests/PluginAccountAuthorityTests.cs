using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PluginAccountAuthorityTests
{
    [Fact]
    public async Task EnterpriseResourceUsesBusinessGrantAndExplicitConsumerBinding()
    {
        await using var f = await Fixture.Create();
        var execution = await f.Legacy.Prepare("search");
        var frozen = JsonSerializer.Deserialize<FrozenConnectorPlan>(execution.PlanJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(f.Resource.ExternalResourceId, frozen.ResourceId);
        Assert.Equal(f.Grant.Id, frozen.AccountAuthority!.BusinessGrantId);
        Assert.Equal(f.Connection.Id, frozen.AccountAuthority.ConnectionId);
        Assert.Contains("owner=workspace-one", frozen.Request.Url);
        await f.Legacy.Service.RevalidateAsync(f.Organization, f.Legacy.Requester.Id, execution.Id, execution.PlanHash, default);
    }

    [Theory]
    [InlineData("grant-revoke")]
    [InlineData("grant-revision")]
    [InlineData("grant-business")]
    [InlineData("grant-capability")]
    [InlineData("grant-digest")]
    [InlineData("grant-policy")]
    [InlineData("selected-resource")]
    [InlineData("account-disconnect")]
    [InlineData("account-revision")]
    [InlineData("unproven-subject")]
    [InlineData("unproven-identity")]
    [InlineData("resource-revision")]
    [InlineData("resource-unavailable")]
    [InlineData("resource-expired")]
    [InlineData("authorization-revision")]
    [InlineData("authorization-disconnect")]
    [InlineData("authorization-other-account")]
    [InlineData("resource-proof")]
    [InlineData("resource-proof-revision")]
    [InlineData("binding-resource")]
    [InlineData("binding-grant")]
    [InlineData("binding-connection")]
    public async Task EveryIndependentAuthorityIsRecheckedBeforeExecutionAndResultDelivery(string change)
    {
        await using var f = await Fixture.Create();
        var execution = await f.Legacy.Prepare("search");
        switch (change)
        {
            case "grant-revoke": f.Grant.RevokedAt = DateTimeOffset.UtcNow; break;
            case "grant-revision": f.Grant.Revision++; break;
            case "grant-business": f.Grant.OrganizationId = Guid.NewGuid(); break;
            case "grant-capability": f.Grant.CapabilitiesJson = "[]"; break;
            case "grant-digest": f.Grant.PackageDigest = new string('b', 64); break;
            case "grant-policy": f.Grant.PolicyVersion = "other"; break;
            case "selected-resource": f.Grant.SelectedResourceIdsJson = JsonSerializer.Serialize(new[] { Guid.NewGuid() }); break;
            case "account-disconnect": f.Group.State = PluginAccountState.Disconnecting; break;
            case "account-revision": f.Group.Revision++; break;
            case "unproven-subject": f.Group.VerifiedSubject = "name@example.com"; break;
            case "unproven-identity": f.Group.IdentityKind = PluginAccountIdentityKind.ProviderSubject; break;
            case "resource-revision": f.Resource.Revision++; break;
            case "resource-unavailable": f.Resource.IsAvailable = false; break;
            case "resource-expired": f.Resource.VerifiedAt = DateTimeOffset.UtcNow.AddDays(-31); break;
            case "authorization-revision": f.Connection.AuthorizationRevision++; break;
            case "authorization-disconnect": f.Connection.Status = PluginConnectionStatus.ReauthorizationRequired; break;
            case "authorization-other-account": f.Connection.AccountGroupId = Guid.NewGuid(); break;
            case "resource-proof": f.Proof.IsAvailable = false; break;
            case "resource-proof-revision": f.Proof.Revision++; break;
            case "binding-resource": f.Binding.PluginAccountResourceId = Guid.NewGuid(); break;
            case "binding-grant": f.Binding.PluginAccountBusinessGrantId = Guid.NewGuid(); break;
            case "binding-connection": f.Binding.PluginAccountConnectionId = Guid.NewGuid(); break;
        }
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Service.RevalidateAsync(f.Organization,
            f.Legacy.Requester.Id, execution.Id, execution.PlanHash, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Service.ValidateResultAuthorityAsync(f.Organization,
            f.Legacy.Requester.Id, execution.Id, execution.PlanHash, default));
    }

    [Fact]
    public async Task WholeAccountRequiresAcknowledgementButStillDoesNotSelectFutureResourcesForAnAgent()
    {
        await using var f = await Fixture.Create();
        f.Grant.Scope = PluginAccountGrantScope.WholeAccount; f.Grant.SelectedResourceIdsJson = "[]";
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Prepare("search"));
        f.Grant.FutureResourcesAcknowledgedAt = DateTimeOffset.UtcNow;
        await f.Legacy.Db.SaveChangesAsync();
        var plan = await f.Legacy.Prepare("search");
        Assert.Equal(f.Resource.ExternalResourceId, plan.ResourceId);
        f.Binding.PluginAccountResourceId = null;
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Prepare("search"));
    }

    [Fact]
    public async Task RevokingBusinessGrantDoesNotMutateSharedProviderAuthorization()
    {
        await using var f = await Fixture.Create();
        f.Grant.RevokedAt = DateTimeOffset.UtcNow; f.Grant.Revision++;
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Prepare("search"));
        Assert.Equal(PluginConnectionStatus.Connected, (await f.Legacy.Db.PluginConnections.SingleAsync()).Status);
        Assert.Equal(PluginAccountState.Connected, (await f.Legacy.Db.PluginAccountGroups.SingleAsync()).State);
    }

    [Fact]
    public async Task ConsentAloneCannotAuthorizeAnUnverifiedResource()
    {
        await using var f = await Fixture.Create();
        f.Legacy.Db.PluginAccountResourceAuthorizations.Remove(f.Proof);
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Prepare("search"));
    }

    [Fact]
    public async Task SameNamedAccountIsNotProofOfResourceOwnership()
    {
        await using var f = await Fixture.Create();
        f.Group.DisplayName = f.Resource.DisplayName = "Same account";
        f.Connection.BoundResourceId = "other-workspace";
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Prepare("search"));
    }

    [Fact]
    public async Task BusinessGrantPayloadChangesInvalidateFrozenPlanEvenIfRevisionWasNotAdvanced()
    {
        await using var f = await Fixture.Create(); var plan = await f.Legacy.Prepare("search");
        f.Grant.ApprovedByApplicationUserId = Guid.NewGuid();
        await f.Legacy.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Service.RevalidateAsync(f.Organization,
            f.Legacy.Requester.Id, plan.Id, plan.PlanHash, default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ConnectorPlanServiceTests.Fixture Legacy { get; private init; } = null!;
        public Guid Organization => Legacy.Organization;
        public PluginConnection Connection => Legacy.Connection;
        public PluginAccountGroup Group { get; private init; } = null!;
        public PluginAccountResource Resource { get; private init; } = null!;
        public PluginAccountResourceAuthorization Proof { get; private init; } = null!;
        public PluginAccountBusinessGrant Grant { get; private init; } = null!;
        public AgentCapabilityBinding Binding { get; private init; } = null!;
        public ValueTask DisposeAsync() => Legacy.DisposeAsync();

        public static async Task<Fixture> Create()
        {
            var legacy = await ConnectorPlanServiceTests.Fixture.Create();
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(legacy.Connector.PackageVersion!.ManifestJson, options)!;
            var operation = manifest.ProviderOperations[0] with
            {
                Capability = "example.api.discover.v1",
                Http = manifest.ProviderOperations[0].Http! with { Bootstrap = true, BoundResourceQuery = null, QueryInputs = new Dictionary<string, string>() }
            };
            var declared = manifest.Provides[0] with { Name = operation.Capability };
            manifest = manifest with
            {
                Protocol = new() { MinimumVersion = "2.4", MaximumVersion = "2.x" },
                ProviderOperations = [manifest.ProviderOperations[0], operation], Provides = [manifest.Provides[0], declared],
                Setup = new() { Required = false, EntryFlow = "connect", Flows = [new() { Id = "connect", Title = "Connect",
                    Steps = [new() { Id = "consent", Kind = "oauth-connect", Connection = "account", ScopeSet = "base" }] }] },
                Accounts = new()
                {
                    Connection = "account", SetupFlow = "connect", ResourceType = "example.workspace", ResourceLabel = "Workspace", ResourcesLabel = "Workspaces",
                    DiscoveryCapability = operation.Capability, ResourceProjection = new() { ItemsPointer = "/items", IdPointer = "/id", NamePointer = "/name" },
                    PrivacyPolicyUrl = "https://example.com/privacy", TermsOfServiceUrl = "https://example.com/terms", PolicyVersion = "v1", ConsentPurpose = "Manage chosen workspaces.",
                    Permissions = [new() { Id = "read", Label = "Read", Purpose = "Read workspace", Capabilities = [ConnectorPlanServiceTests.Fixture.Capability], ScopeSets = ["base"] }]
                }
            };
            legacy.Connector.Scope = PluginInstallationScope.System; legacy.Connector.BusinessId = "enterprise";
            legacy.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, options);
            var now = DateTimeOffset.UtcNow;
            var group = new PluginAccountGroup { Id = Guid.NewGuid(), PluginInstallationId = legacy.Connector.Id, DisplayName = "Account",
                ProviderProfile = legacy.Connection.ProviderProfile, State = PluginAccountState.Connected, CreatedAt = now, UpdatedAt = now };
            var resource = new PluginAccountResource { Id = Guid.NewGuid(), AccountGroupId = group.Id, PluginInstallationId = legacy.Connector.Id,
                ResourceType = "example.workspace", ExternalResourceId = "workspace-one", DisplayName = "Workspace", IsAvailable = true, VerifiedAt = now, UpdatedAt = now };
            legacy.Connection.AccountGroupId = group.Id; legacy.Connection.BoundResourceId = null;
            var proof = new PluginAccountResourceAuthorization { ResourceId = resource.Id, ConnectionId = legacy.Connection.Id, IsAvailable = true, VerifiedAt = now };
            var grant = new PluginAccountBusinessGrant { Id = Guid.NewGuid(), PluginInstallationId = legacy.Connector.Id, AccountGroupId = group.Id,
                OrganizationId = legacy.Organization, Scope = PluginAccountGrantScope.SelectedResources,
                SelectedResourceIdsJson = JsonSerializer.Serialize(new[] { resource.Id }), CapabilitiesJson = JsonSerializer.Serialize(new[] { ConnectorPlanServiceTests.Fixture.Capability }),
                PackageDigest = legacy.Connector.PackageVersion.PackageDigest!, PolicyVersion = "v1", ApprovedByApplicationUserId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
            var binding = await legacy.Db.AgentCapabilityBindings.SingleAsync();
            binding.PluginAccountBusinessGrantId = grant.Id; binding.PluginAccountResourceId = resource.Id; binding.PluginAccountConnectionId = legacy.Connection.Id;
            legacy.Db.AddRange(group, resource, proof, grant); await legacy.Db.SaveChangesAsync();
            return new() { Legacy = legacy, Group = group, Resource = resource, Proof = proof, Grant = grant, Binding = binding };
        }
    }
}
