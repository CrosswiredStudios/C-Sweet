using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ConnectorActivationApprovalTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Approved = [ConnectorPlanServiceTests.Fixture.Capability,
        "example.api.categories.v1", "example.api.item.v1", "example.api.upload.v1"];
    private static readonly string[] Declined = Enumerable.Range(1, 6).Select(x => $"example.api.declined{x}.v1").ToArray();

    [Fact]
    public async Task NativeActivationUsesTheFourApprovedBindingsWithoutDemandingSixDeclinedOperations()
    {
        await using var f = await Native();
        var service = new ConnectorBindingService(f.Legacy.Db);
        var choice = Assert.Single(await service.GetChoicesAsync(f.Organization, f.Legacy.Requester.Id, default));
        Assert.Equal(Approved, choice.Capabilities);
        Assert.True(Assert.Single(choice.ResourceChoices).Selected);
        await service.ValidateRequiredBindingsAsync(f.Legacy.Requester, default);
        Assert.Equal(Approved.Order(), (await f.Legacy.Db.AgentCapabilityBindings.Select(x => x.Capability).ToArrayAsync()).Order());
        Assert.Equal(Approved, JsonSerializer.Deserialize<string[]>(f.Legacy.Requester.Grant!.RequiredCapabilitiesJson));
    }

    [Theory]
    [InlineData("missing-approved-base")]
    [InlineData("no-consumer-grant")]
    [InlineData("stale-consumer-revision")]
    [InlineData("revoked-binding")]
    [InlineData("unapproved-binding")]
    [InlineData("connector-digest")]
    [InlineData("connector-disabled")]
    [InlineData("wrong-tenant")]
    [InlineData("revoked-business-grant")]
    [InlineData("business-capability")]
    [InlineData("disconnected")]
    [InlineData("unavailable-resource")]
    [InlineData("unavailable-proof")]
    [InlineData("wrong-resource")]
    [InlineData("wrong-connection")]
    public async Task NarrowedActivationStillRejectsMissingOrChangedAuthority(string change)
    {
        await using var f = await Native();
        switch (change)
        {
            case "missing-approved-base": f.Legacy.Db.Remove(await f.Legacy.Db.AgentCapabilityBindings.SingleAsync(x => x.Capability == Approved[1])); break;
            case "no-consumer-grant": f.Legacy.Requester.Grant!.RequiredCapabilitiesJson = "[]"; break;
            case "stale-consumer-revision": f.Legacy.Requester.Grant!.GrantRevision++; break;
            case "revoked-binding": f.Binding.RevokedAt = DateTimeOffset.UtcNow; break;
            case "unapproved-binding": f.Binding.Capability = Declined[0]; break;
            case "connector-digest": f.Legacy.Connector.PackageVersion!.PackageDigest = new string('c', 64); break;
            case "connector-disabled": f.Legacy.Connector.IsEnabled = false; break;
            case "wrong-tenant": f.Grant.OrganizationId = Guid.NewGuid(); break;
            case "revoked-business-grant": f.Grant.RevokedAt = DateTimeOffset.UtcNow; break;
            case "business-capability": f.Grant.CapabilitiesJson = "[]"; break;
            case "disconnected": f.Connection.Status = PluginConnectionStatus.Revoked; break;
            case "unavailable-resource": f.Resource.IsAvailable = false; break;
            case "unavailable-proof": f.Proof.IsAvailable = false; break;
            case "wrong-resource": f.Binding.PluginAccountResourceId = Guid.NewGuid(); break;
            case "wrong-connection": f.Binding.PluginAccountConnectionId = Guid.NewGuid(); break;
        }
        await f.Legacy.Db.SaveChangesAsync();
        var service = new ConnectorBindingService(f.Legacy.Db);
        var choices = await service.GetChoicesAsync(f.Organization, f.Legacy.Requester.Id, default);
        Assert.DoesNotContain(choices.SelectMany(x => x.ResourceChoices), x => x.Selected);
        var error = await Record.ExceptionAsync(() => service.ValidateRequiredBindingsAsync(f.Legacy.Requester, default));
        Assert.True(error is InvalidOperationException or UnauthorizedAccessException, error?.ToString() ?? "Authority was incorrectly accepted.");
    }

    [Fact]
    public async Task LegacyChoicesBindingAndActivationRespectTheSameApprovedSubset()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        AddDeclinedRequirements(f.Requester);
        var service = new ConnectorBindingService(f.Db);
        var choice = Assert.Single(await service.GetChoicesAsync(f.Organization, f.Requester.Id, default));
        Assert.Equal([ConnectorPlanServiceTests.Fixture.Capability], choice.Capabilities);
        Assert.True(Assert.Single(choice.Choices).CanSelect);
        await service.BindAsync(f.Organization, f.Requester.Id, "account", f.Connector.Id, default);
        await service.ValidateRequiredBindingsAsync(f.Requester, default);
        Assert.Equal(ConnectorPlanServiceTests.Fixture.Capability, (await f.Db.AgentCapabilityBindings.SingleAsync()).Capability);
    }

    [Fact]
    public async Task UnboundOptionalOperationDoesNotBlockBaseSetupAndStillCannotPrepareAnExecution()
    {
        await using var f = await Native();
        var control = await f.Legacy.Service.PrepareAsync(f.Organization, f.Legacy.Requester.Id, Approved[3],
            ConnectorPlanServiceTests.Fixture.Input("synthetic"), "optional-bound-control", default);
        Assert.True(await f.Legacy.Service.IsAvailableAsync(f.Organization, f.Legacy.Requester.Id, Approved[3], default));
        f.Legacy.Db.Remove(await f.Legacy.Db.AgentCapabilityBindings.SingleAsync(x => x.Capability == Approved[3]));
        await f.Legacy.Db.SaveChangesAsync();
        var service = new ConnectorBindingService(f.Legacy.Db);
        await service.ValidateRequiredBindingsAsync(f.Legacy.Requester, default);
        Assert.True(Assert.Single(Assert.Single(await service.GetChoicesAsync(f.Organization, f.Legacy.Requester.Id, default)).ResourceChoices).Selected);
        Assert.False(await f.Legacy.Service.IsAvailableAsync(f.Organization, f.Legacy.Requester.Id, Approved[3], default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Legacy.Service.PrepareAsync(f.Organization,
            f.Legacy.Requester.Id, Approved[3], ConnectorPlanServiceTests.Fixture.Input("synthetic"), "optional-unbound", default));
        Assert.Equal(control.Id, (await f.Legacy.Db.ConnectorExecutions.SingleAsync()).Id);
    }

    [Fact]
    public async Task RemoteMcpRetainsItsStricterAllDeclaredCapabilitiesApprovalPolicy()
    {
        await using var f = await RemoteMcpNativeFlowTests.Fixture.Create();
        var protection = new EphemeralDataProtectionProvider();
        f.Service = new RemoteMcpAccountService(f.Db, protection);
        await f.Grant(await f.Connect());
        var service = new ConnectorBindingService(f.Db, protection);
        await service.ValidateRequiredBindingsAsync(f.Consumer, default);
        var binding = await f.Db.AgentCapabilityBindings.FirstAsync();
        f.Consumer.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { binding.Capability });
        f.Db.AgentCapabilityBindings.RemoveRange(await f.Db.AgentCapabilityBindings.Where(x => x.Id != binding.Id).ToArrayAsync());
        await f.Db.SaveChangesAsync();
        Assert.False(await f.Service.IsAvailableAsync(f.Business, f.Consumer.Id, binding.Capability, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateRequiredBindingsAsync(f.Consumer, default));
    }

    private static void AddDeclinedRequirements(AgentInstallation requester)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(requester.PackageVersion!.ManifestJson, Json)!;
        requester.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest with
        {
            Requires = [.. manifest.Requires, .. Declined.Select(name => manifest.Requires[0] with { Name = name })]
        }, Json);
    }

    internal static async Task<PluginAccountAuthorityTests.Fixture> Native()
    {
        var f = await PluginAccountAuthorityTests.Fixture.Create();
        var provider = JsonSerializer.Deserialize<PluginManifest>(f.Legacy.Connector.PackageVersion!.ManifestJson, Json)!;
        var read = provider.ProviderOperations[0];
        var connection = provider.Connections[0];
        provider = provider with
        {
            Provides = [.. provider.Provides, .. Approved.Skip(1).Select(name => provider.Provides[0] with { Name = name })],
            ProviderOperations = [.. provider.ProviderOperations, .. Approved.Skip(1).Select(name => read with
            {
                Capability = name, Http = read.Http! with { ScopeSets = name == Approved[3] ? ["publishing"] : ["base"] }
            })],
            Connections = [connection with { ScopeSets = [connection.ScopeSets[0] with { Required = true },
                new() { Id = "publishing", Scopes = ["upload"], Required = false }] }],
            Accounts = provider.Accounts! with
            {
                Permissions = [provider.Accounts!.Permissions[0] with { Capabilities = Approved.Take(3).ToArray() },
                    provider.Accounts.Permissions[0] with { Id = "publish", Label = "Publish", Capabilities = [Approved[3]], ScopeSets = ["publishing"] }]
            }
        };
        PluginManifestReader.ValidateConnectorContracts(provider);
        f.Legacy.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(provider, Json);
        var consumer = JsonSerializer.Deserialize<PluginManifest>(f.Legacy.Requester.PackageVersion!.ManifestJson, Json)!;
        f.Legacy.Requester.PackageVersion.ManifestJson = JsonSerializer.Serialize(consumer with
        {
            Requires = Approved.Select(name => consumer.Requires[0] with { Name = name }).ToArray()
        }, Json);
        AddDeclinedRequirements(f.Legacy.Requester);
        f.Legacy.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(Approved);
        f.Legacy.Connector.Grant!.ProvidedCapabilitiesJson = JsonSerializer.Serialize(Approved);
        f.Grant.CapabilitiesJson = JsonSerializer.Serialize(Approved);
        f.Connection.GrantedScopesJson = "[\"read\",\"upload\"]";
        foreach (var capability in Approved.Skip(1))
            f.Legacy.Db.AgentCapabilityBindings.Add(new()
            {
                Id = Guid.NewGuid(), RequesterInstallationId = f.Legacy.Requester.Id, OrganizationId = f.Organization.ToString("D"),
                ProviderInstallationId = f.Legacy.Connector.Id, DependencyId = "account", Capability = capability,
                GrantRevision = f.Legacy.Requester.Grant.GrantRevision, ProviderPackageDigest = f.Legacy.Connector.PackageVersion.PackageDigest,
                PluginAccountBusinessGrantId = f.Grant.Id, PluginAccountResourceId = f.Resource.Id, PluginAccountConnectionId = f.Connection.Id,
                Origin = AgentCapabilityBindingOrigins.Explicit
            });
        await f.Legacy.Db.SaveChangesAsync();
        return f;
    }
}
