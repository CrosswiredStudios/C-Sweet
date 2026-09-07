using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.UnitTests;

public sealed class ConnectorSetupSelectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SelectionBindsAuthenticatedIdentityAndIgnoresBrowserName()
    {
        await using var fixture = await Create();
        var result = await Service(fixture).CompleteStepAsync(fixture.Organization, fixture.Connector.Id,
            "select", Request("account-a"));
        Assert.Equal("account-a", fixture.Connection.BoundResourceId);
        Assert.Equal("Verified provider name", fixture.Connection.ExternalAccountName);
        Assert.Equal("Verified provider name", result.Values["selectedAccountName"].GetString());
        Assert.Contains("select", result.CompletedStepIds);
        Assert.Null(result.CurrentStepId);
        Assert.DoesNotContain("connectedChannelId", fixture.Connector.SetupDataJson);
    }

    [Fact]
    public async Task UnknownAccountCannotBeBound()
    {
        await using var fixture = await Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fixture).CompleteStepAsync(
            fixture.Organization, fixture.Connector.Id, "select", Request("attacker-account")));
        Assert.Null(fixture.Connection.BoundResourceId);
        Assert.Equal("select", fixture.Connector.SetupStepId);
    }

    [Fact]
    public async Task ReadyConnectionDisplaysDeclaredSettingsInsteadOfSetupEntry()
    {
        await using var fixture = await Create();
        fixture.Connector.SetupState = PluginSetupState.Ready;
        fixture.Connector.SetupStepId = null;
        await fixture.Db.SaveChangesAsync();
        var result = await Service(fixture).GetAsync(fixture.Organization, fixture.Connector.Id);
        Assert.Equal("settings", result.FlowId);
        Assert.Equal("permissions", Assert.Single(result.Flow.Steps).Id);
    }

    private static CompletePluginSetupStepRequest Request(string id) => new(
        new Dictionary<string, JsonElement>
        {
            ["selectedAccountId"] = JsonSerializer.SerializeToElement(id),
            ["selectedAccountName"] = JsonSerializer.SerializeToElement("Browser-controlled name")
        }, "selection-1");

    [Fact]
    public async Task RepeatedActivationValidatesEntryFlowWithoutReactivating()
    {
        await using var fixture = await Create();
        var service = Service(fixture);
        await service.CompleteStepAsync(fixture.Organization, fixture.Connector.Id, "select", Request("account-a"));
        Assert.True((await service.ActivateAsync(fixture.Organization, Guid.NewGuid(), fixture.Connector.Id)).Ready);
        var activatedAt = fixture.Connector.UpdatedAt;
        Assert.True((await service.ActivateAsync(fixture.Organization, Guid.NewGuid(), fixture.Connector.Id)).Ready);
        Assert.Equal(activatedAt, fixture.Connector.UpdatedAt);
        fixture.Connection.Status = PluginConnectionStatus.Revoked;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateAsync(
            fixture.Organization, Guid.NewGuid(), fixture.Connector.Id));
    }

    private static PluginSetupService Service(ConnectorPlanServiceTests.Fixture fixture) => new(
        fixture.Db, null!, new EphemeralDataProtectionProvider(), null!, new Bootstrap(),
        null!, new Configuration(), null!, new Audit());

    private static async Task<ConnectorPlanServiceTests.Fixture> Create()
    {
        var fixture = await ConnectorPlanServiceTests.Fixture.Create();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(fixture.Connector.PackageVersion!.ManifestJson, JsonOptions)!;
        manifest = manifest with
        {
            Setup = new() { Required = true, EntryFlow = "connect", Flows = [
                new() { Id = "connect", Steps = [new() { Id = "select", Kind = "account-selector", Connection = "account" }] },
                new() { Id = "settings", Steps = [new() { Id = "permissions", Kind = "permission-summary" }] }
            ] },
            Ui = [new() { Kind = "personal-settings", Flow = "settings" }]
        };
        fixture.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        fixture.Connector.SetupState = PluginSetupState.NeedsSetup;
        fixture.Connector.SetupFlowId = "connect";
        fixture.Connector.SetupStepId = "select";
        fixture.Connection.BoundResourceId = null;
        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    private sealed class Bootstrap : IPluginBootstrapCapabilityService
    {
        public Task<JsonElement> InvokeAsync(Guid organizationId, Guid installationId, string stepId,
            JsonElement arguments, CancellationToken cancellationToken = default) => Task.FromResult(
                JsonSerializer.SerializeToElement(new { accounts = new[] { new { id = "account-a", name = "Verified provider name" } } }));
    }

    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary,
            string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Configuration : IAgentInstallationConfigurationService
    {
        public Task<AgentInstallationConfigurationSnapshot?> GetAsync(Guid installationId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentInstallationConfigurationSnapshot?>(null);
        public Task<AgentInstallationConfigurationSnapshot> SaveAsync(Guid installationId, string schemaVersion,
            IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account selection must not write provider-specific agent configuration.");
    }
}
