using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class NativeEnterpriseConnectorCatalogTests
{
    private const string Capability = ConnectorPlanServiceTests.Fixture.Capability;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExplicitBusinessResourceBindingExposesTheEnterpriseReadWithoutExecutingIt()
    {
        await using var f = await PluginAccountAuthorityTests.Fixture.Create();
        Assert.Equal("enterprise", f.Legacy.Connector.BusinessId);
        Assert.NotEqual(f.Legacy.Requester.BusinessId, f.Legacy.Connector.BusinessId);
        Assert.True(await f.Legacy.Service.IsAvailableAsync(f.Organization, f.Legacy.Requester.Id, Capability, default));
        var catalog = new McpToolCatalog([], credentials: f.Legacy.Credentials);
        var session = Session(f.Legacy);
        var tool = Assert.Single(await ConnectorTools(catalog.ListAsync(session, f.Legacy.Db, default)));
        Assert.Equal(Capability, tool.Capability);
        Assert.Equal(f.Legacy.Connector.Id, tool.ProviderInstallationId);
        var found = Assert.IsType<McpToolDescriptor>(await catalog.FindAsync(tool.Name, session, f.Legacy.Db, default));
        Assert.Equal(tool.Name, found.Name);
        Assert.Equal(tool.Capability, found.Capability);
        Assert.Equal(tool.ProviderInstallationId, found.ProviderInstallationId);
        Assert.Equal(tool.InputSchema.GetRawText(), found.InputSchema.GetRawText());
        Assert.Equal(tool.OutputSchema?.GetRawText(), found.OutputSchema?.GetRawText());
        Assert.Empty(await f.Legacy.Db.ConnectorExecutions.ToArrayAsync());
        Assert.Empty(await f.Legacy.Db.PluginOperationalStates.ToArrayAsync());
        Assert.False(f.Legacy.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("session-business")]
    [InlineData("session-revision")]
    [InlineData("session-capability")]
    [InlineData("binding-business")]
    [InlineData("binding-revision")]
    [InlineData("binding-revoked")]
    [InlineData("binding-digest")]
    [InlineData("missing-grant")]
    [InlineData("missing-resource")]
    [InlineData("missing-connection")]
    [InlineData("wrong-grant")]
    [InlineData("wrong-resource")]
    [InlineData("wrong-connection")]
    [InlineData("consumer-business")]
    [InlineData("consumer-revision")]
    [InlineData("consumer-capability")]
    [InlineData("consumer-disabled")]
    [InlineData("consumer-retired")]
    [InlineData("declined-dependency")]
    [InlineData("provider-capability")]
    [InlineData("provider-disabled")]
    [InlineData("provider-retired")]
    [InlineData("provider-scope")]
    [InlineData("package-digest")]
    [InlineData("package-publisher")]
    [InlineData("package-version")]
    [InlineData("business-grant-revoked")]
    [InlineData("business-grant-business")]
    [InlineData("business-capability")]
    [InlineData("resource-unavailable")]
    [InlineData("resource-not-selected")]
    [InlineData("resource-expired")]
    [InlineData("proof-unavailable")]
    [InlineData("connection-revoked")]
    [InlineData("connection-scope")]
    [InlineData("group-disconnecting")]
    [InlineData("profile-revoked")]
    [InlineData("system-agent-package")]
    [InlineData("system-service-package")]
    [InlineData("system-agent-manifest")]
    [InlineData("system-remote-manifest")]
    [InlineData("system-legacy-manifest")]
    public async Task EnterpriseReadRemainsHiddenWhenAnyIndependentAuthorityIsMissingOrChanged(string change)
    {
        await using var f = await PluginAccountAuthorityTests.Fixture.Create();
        var session = Session(f.Legacy);
        switch (change)
        {
            case "session-business": session = session with { BusinessId = Guid.NewGuid().ToString("D") }; break;
            case "session-revision": session = session with { Grant = session.Grant with { Revision = session.Grant.Revision + 1 } }; break;
            case "session-capability": session = session with { Grant = session.Grant with { RequiredCapabilities = new HashSet<string>() } }; break;
            case "binding-business": f.Binding.OrganizationId = Guid.NewGuid().ToString("D"); break;
            case "binding-revision": f.Binding.GrantRevision++; break;
            case "binding-revoked": f.Binding.RevokedAt = DateTimeOffset.UtcNow; break;
            case "binding-digest": f.Binding.ProviderPackageDigest = new string('b', 64); break;
            case "missing-grant": f.Binding.PluginAccountBusinessGrantId = null; break;
            case "missing-resource": f.Binding.PluginAccountResourceId = null; break;
            case "missing-connection": f.Binding.PluginAccountConnectionId = null; break;
            case "wrong-grant": f.Binding.PluginAccountBusinessGrantId = Guid.NewGuid(); break;
            case "wrong-resource": f.Binding.PluginAccountResourceId = Guid.NewGuid(); break;
            case "wrong-connection": f.Binding.PluginAccountConnectionId = Guid.NewGuid(); break;
            case "consumer-business": f.Legacy.Requester.BusinessId = Guid.NewGuid().ToString("D"); break;
            case "consumer-revision": f.Legacy.Requester.Grant!.GrantRevision++; break;
            case "consumer-capability": f.Legacy.Requester.Grant!.RequiredCapabilitiesJson = "[]"; break;
            case "consumer-disabled": f.Legacy.Requester.IsEnabled = false; break;
            case "consumer-retired": f.Legacy.Requester.RevisionStatus = PluginRevisionStatus.Retired; break;
            case "declined-dependency": Rewrite(f.Legacy.Requester, m => m with { Requires = [] }); break;
            case "provider-capability": f.Legacy.Connector.Grant!.ProvidedCapabilitiesJson = "[]"; break;
            case "provider-disabled": f.Legacy.Connector.IsEnabled = false; break;
            case "provider-retired": f.Legacy.Connector.RevisionStatus = PluginRevisionStatus.Retired; break;
            case "provider-scope": f.Legacy.Connector.Scope = PluginInstallationScope.Organization; break;
            case "package-digest": f.Legacy.Connector.PackageVersion!.PackageDigest = new string('c', 64); break;
            case "package-publisher": f.Legacy.Connector.PackageVersion!.PublisherId = "example.other"; break;
            case "package-version": f.Legacy.Connector.PackageVersion!.Version = "0.2.0"; break;
            case "business-grant-revoked": f.Grant.RevokedAt = DateTimeOffset.UtcNow; break;
            case "business-grant-business": f.Grant.OrganizationId = Guid.NewGuid(); break;
            case "business-capability": f.Grant.CapabilitiesJson = "[]"; break;
            case "resource-unavailable": f.Resource.IsAvailable = false; break;
            case "resource-not-selected": f.Grant.SelectedResourceIdsJson = "[]"; break;
            case "resource-expired": f.Resource.VerifiedAt = DateTimeOffset.UtcNow.AddDays(-31); break;
            case "proof-unavailable": f.Proof.IsAvailable = false; break;
            case "connection-revoked": f.Connection.Status = PluginConnectionStatus.Revoked; break;
            case "connection-scope": f.Connection.GrantedScopesJson = "[]"; break;
            case "group-disconnecting": f.Group.State = PluginAccountState.Disconnecting; break;
            case "profile-revoked": (await f.Legacy.Db.ConnectorProfileApprovals.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow; break;
            case "system-agent-package": f.Legacy.Connector.PackageVersion!.PluginKind = PluginKind.Agent; break;
            case "system-service-package": f.Legacy.Connector.PackageVersion!.PluginKind = PluginKind.Service; break;
            case "system-agent-manifest": Rewrite(f.Legacy.Connector, m => m with { Kind = "agent" }); break;
            case "system-remote-manifest": Rewrite(f.Legacy.Connector, m => m with { Kind = "remote-mcp" }); break;
            case "system-legacy-manifest": Rewrite(f.Legacy.Connector, m => m with { Accounts = null }); break;
        }
        await f.Legacy.Db.SaveChangesAsync();
        Assert.Empty(await ConnectorTools(new McpToolCatalog([], credentials: f.Legacy.Credentials).ListAsync(session, f.Legacy.Db, default)));
        Assert.Empty(await f.Legacy.Db.ConnectorExecutions.ToArrayAsync());
        Assert.False(f.Legacy.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task HiddenNativeRequirementRemainsCallableByCodeWithoutBecomingModelVisible()
    {
        await using var f = await PluginAccountAuthorityTests.Fixture.Create();
        Rewrite(f.Legacy.Requester, m => m with { Requires = [m.Requires[0] with { ModelVisible = false }] });
        await f.Legacy.Db.SaveChangesAsync();
        var catalog = new McpToolCatalog([], credentials: f.Legacy.Credentials);
        var session = Session(f.Legacy);
        var tool = Assert.Single(await ConnectorTools(catalog.ListAsync(session, f.Legacy.Db, default)));
        Assert.False(tool.ModelVisible);
        Assert.NotNull(await catalog.FindAsync(tool.Name, session, f.Legacy.Db, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrganizationConnectorVisibilityIsUnchanged(bool otherBusiness)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        if (otherBusiness) f.Connector.BusinessId = Guid.NewGuid().ToString("D");
        await f.Db.SaveChangesAsync();
        var tools = await ConnectorTools(new McpToolCatalog([], credentials: f.Credentials).ListAsync(Session(f), f.Db, default));
        Assert.Equal(otherBusiness ? 0 : 1, tools.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameBusinessSystemGenericProviderCompatibilityDoesNotCreateCrossBusinessAccess(bool otherBusiness)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Connector.PackageVersion!.ManifestJson, Json)!;
        f.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(new PluginManifest
        { Id = manifest.Id, Kind = "agent", Name = "Example service agent", Version = "0.1.0", Provides = manifest.Provides }, Json);
        f.Connector.PackageVersion.PluginKind = PluginKind.Agent;
        f.Connector.Scope = PluginInstallationScope.System;
        f.Connector.BusinessId = "enterprise";
        f.Requester.BusinessId = "enterprise";
        var binding = await f.Db.AgentCapabilityBindings.SingleAsync();
        binding.OrganizationId = "enterprise";
        if (otherBusiness)
        {
            f.Requester.BusinessId = binding.OrganizationId = f.Organization.ToString("D");
            // Exercise the broadened candidate query, not merely its null-ID prefilter.
            binding.PluginAccountBusinessGrantId = Guid.NewGuid();
            binding.PluginAccountResourceId = Guid.NewGuid();
            binding.PluginAccountConnectionId = Guid.NewGuid();
        }
        await f.Db.SaveChangesAsync();
        var tools = await ConnectorTools(new McpToolCatalog([], credentials: f.Credentials).ListAsync(Session(f), f.Db, default));
        Assert.Equal(otherBusiness ? 0 : 1, tools.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedRemoteMcpRemainsOrganizationScoped(bool otherBusiness)
    {
        await using var f = await RemoteMcpNativeFlowTests.Fixture.Create();
        await f.Grant(await f.Connect());
        var session = new AgentSession("session", "example.agent", f.Consumer.Id.ToString("D"), f.Business.ToString("D"), "runtime", "tick",
            new(new HashSet<string>(), new HashSet<string>(),
                (JsonSerializer.Deserialize<string[]>(f.Consumer.Grant!.RequiredCapabilitiesJson) ?? []).ToHashSet(StringComparer.Ordinal), f.Consumer.Grant.GrantRevision));
        var catalog = new McpToolCatalog([], remoteMcp: f.Service);
        Assert.NotEmpty(await ConnectorTools(catalog.ListAsync(session, f.Db, default)));
        if (otherBusiness)
        {
            f.Provider.BusinessId = Guid.NewGuid().ToString("D");
            f.Provider.Scope = PluginInstallationScope.System;
            foreach (var binding in await f.Db.AgentCapabilityBindings.ToArrayAsync())
            { binding.PluginAccountBusinessGrantId = Guid.NewGuid(); binding.PluginAccountResourceId = Guid.NewGuid(); binding.PluginAccountConnectionId = Guid.NewGuid(); }
            await f.Db.SaveChangesAsync();
            Assert.Empty(await ConnectorTools(catalog.ListAsync(session, f.Db, default)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidNativeMediaAndSpoofedReadMediaAreNeverExposedAsDirectReadTools(bool spoofRead)
    {
        await using var f = await ConnectorMediaTransferTests.Fixture.Create(native: true);
        PluginManifestReader.ValidateConnectorContracts(JsonSerializer.Deserialize<PluginManifest>(f.Inner.Connector.PackageVersion!.ManifestJson, Json)!);
        var executions = await f.Inner.Db.ConnectorExecutions.CountAsync();
        if (spoofRead)
        {
            Rewrite(f.Inner.Connector, m => m with { ProviderOperations = [m.ProviderOperations[0] with
                { Effect = "read", Http = m.ProviderOperations[0].Http! with { Method = "GET" } }, .. m.ProviderOperations.Skip(1)] });
            await f.Inner.Db.SaveChangesAsync();
            Assert.Throws<JsonException>(() => PluginManifestReader.ValidateConnectorContracts(
                JsonSerializer.Deserialize<PluginManifest>(f.Inner.Connector.PackageVersion!.ManifestJson, Json)!));
        }
        Assert.Empty(await ConnectorTools(new McpToolCatalog([], credentials: f.Inner.Credentials).ListAsync(Session(f.Inner), f.Inner.Db, default)));
        Assert.Equal(executions, await f.Inner.Db.ConnectorExecutions.CountAsync());
        Assert.Empty(f.Provider.Steps);
        Assert.Empty(f.Provider.Chunks);
    }

    private static AgentSession Session(ConnectorPlanServiceTests.Fixture f) =>
        new("session", "example.agent", f.Requester.Id.ToString("D"), f.Requester.BusinessId, "runtime", "tick",
            new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { Capability }, f.Requester.Grant!.GrantRevision));

    private static void Rewrite(AgentInstallation installation, Func<PluginManifest, PluginManifest> update) =>
        installation.PackageVersion!.ManifestJson = JsonSerializer.Serialize(update(
            JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion.ManifestJson, Json)!), Json);

    // Every agent session also lists its own implicit self-read tools (posture, harness status);
    // these tests are about connector visibility, so count connector tools only.
    private static async Task<McpToolDescriptor[]> ConnectorTools(Task<IReadOnlyList<McpToolDescriptor>> listing) =>
        (await listing).Where(tool => !McpToolCatalog.SelfReadCapabilities.Contains(tool.Capability)).ToArray();
}
