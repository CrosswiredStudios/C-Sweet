using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentDefinitionLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_OfflineProviderRetainsDefaultsAndEmployeeOverrides(bool alreadyBuilt)
    {
        await using var db = CreateDb();
        var current = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        SignForUpgradeTest(current);
        SetUpgradeConfiguration(current,
            new { key = "provider", type = "llmProvider", label = "Provider", required = true },
            new { key = "model", type = "llmModel", label = "Model", required = true, dependsOnFieldKey = "provider" });
        var provider = new LlmProviderProfile
        {
            Id = Guid.NewGuid(), Name = "Offline provider", IsEnabled = true,
            BaseUrl = "http://localhost:8888/v1", DefaultChatModel = "different-default-model"
        };
        var definition = SeedDefinition(db, current, ActivationMode.OnDemand);
        definition.Configuration!.SettingsJson = JsonSerializer.Serialize(new
        {
            provider = provider.Id.ToString("D"), model = "previously-selected-model"
        });
        var hire = RuntimeInstallation(current, Guid.NewGuid().ToString("D"));
        hire.AgentDefinitionId = definition.Id;
        hire.Configuration!.SettingsJson = "{\"model\":\"employee-selected-model\"}";
        definition.Installations.Add(hire);
        var update = CreateUpdatePackage(current, "1.1.0");
        if (alreadyBuilt)
        {
            update.Status = AgentPackageVersionStatus.Built;
            SignForUpgradeTest(update);
        }
        db.AddRange(provider, hire, update);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var catalog = new OfflineUpgradeModelCatalog();
        var builds = new RecordingBuildService(db);

        var result = await new AgentDefinitionService(db, new TestAuditEventWriter(), builds, catalog)
            .UpdateAsync(definition.Id, new UpdateAgentDefinitionRequest(update.Id));

        Assert.Equal(0, catalog.Calls);
        Assert.Equal(update.Id, result.PackageVersionId);
        Assert.Equal(alreadyBuilt ? "Available" : "Building", result.Status);
        Assert.Equal(alreadyBuilt, result.IsAvailableForHire);
        var saved = await db.AgentDefinitionConfigurations.SingleAsync();
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(saved.SettingsJson)!;
        Assert.Equal(provider.Id.ToString("D"), settings["provider"].GetString());
        Assert.Equal("previously-selected-model", settings["model"].GetString());
        Assert.Equal(2, saved.Revision);
        var employee = await db.AgentInstallations.Include(x => x.Configuration).SingleAsync();
        Assert.Equal(alreadyBuilt ? update.Id : current.Id, employee.PackageVersionId);
        Assert.Equal("{\"model\":\"employee-selected-model\"}", employee.Configuration!.SettingsJson);
        Assert.Equal("employee-selected-model",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(employee.Configuration.SettingsJson)!["model"].GetString());
        Assert.Equal(alreadyBuilt ? (Guid?)null : update.Id, builds.QueuedPackageVersionId);
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("type-change")]
    [InlineData("removed-option")]
    [InlineData("range-change")]
    [InlineData("disabled-provider")]
    [InlineData("deleted-provider")]
    public async Task Update_IncompatibleSettingsInstallsPackageAndRequiresConfiguration(string incompatibility)
    {
        await using var db = CreateDb();
        var current = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        SignForUpgradeTest(current);
        var definition = SeedDefinition(db, current, ActivationMode.OnDemand);
        var hire = RuntimeInstallation(current, Guid.NewGuid().ToString("D"));
        hire.AgentDefinitionId = definition.Id;
        definition.Installations.Add(hire);
        var update = CreateUpdatePackage(current, "1.1.0");
        update.Status = AgentPackageVersionStatus.Built;
        SignForUpgradeTest(update);
        object? retainedValue = incompatibility switch
        {
            "missing-required" => null,
            "type-change" => "old text value",
            "removed-option" => "old-option",
            "range-change" => 50,
            _ => Guid.NewGuid().ToString("D")
        };
        object field = incompatibility switch
        {
            "type-change" => new { key = "setting", type = "boolean", label = "Setting", required = true },
            "removed-option" => new { key = "setting", type = "select", label = "Setting", required = true,
                options = new[] { new { value = "new-option", label = "New option" } } },
            "range-change" => new { key = "setting", type = "number", label = "Setting", required = true, maximum = 10 },
            "disabled-provider" or "deleted-provider" => new { key = "setting", type = "llmProvider", label = "Setting", required = true },
            _ => new { key = "setting", type = "text", label = "Setting", required = true }
        };
        SetUpgradeConfiguration(update, field);
        if (retainedValue is not null)
            definition.Configuration!.SettingsJson = JsonSerializer.Serialize(new { setting = retainedValue });
        if (incompatibility == "disabled-provider")
            db.Add(new LlmProviderProfile { Id = Guid.Parse((string)retainedValue!), Name = "Disabled", IsEnabled = false });
        db.AddRange(hire, update);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var catalog = new OfflineUpgradeModelCatalog();

        var result = await new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db), catalog)
            .UpdateAsync(definition.Id, new UpdateAgentDefinitionRequest(update.Id));

        Assert.Equal(update.Id, result.PackageVersionId);
        Assert.Equal("NeedsConfiguration", result.Status);
        Assert.False(result.IsAvailableForHire);
        Assert.Equal(0, catalog.Calls);
        Assert.Equal(current.Id, (await db.AgentInstallations.SingleAsync()).PackageVersionId);
        var saved = await db.AgentDefinitionConfigurations.SingleAsync();
        Assert.Equal(definition.Configuration!.SettingsJson, saved.SettingsJson);
        Assert.Equal(2, saved.Revision);

        if (incompatibility == "missing-required")
        {
            await new AgentInstallationConfigurationService(db, new TestAuditEventWriter()).SaveDefinitionAsync(
                definition.Id, new PutAgentDefinitionConfigurationRequest("1",
                    new Dictionary<string, JsonElement> { ["setting"] = JsonSerializer.SerializeToElement("configured") }, 2));
            var repaired = await db.AgentDefinitions.SingleAsync();
            Assert.Equal(AgentDefinitionStatus.Available, repaired.Status);
            Assert.True(repaired.IsAvailableForHire);
            await new AgentDefinitionInstallationSynchronizer(db, new TestAuditEventWriter()).SynchronizeAsync(definition.Id);
            Assert.Equal(update.Id, (await db.AgentInstallations.SingleAsync()).PackageVersionId);
        }
    }

    [Fact]
    public async Task SaveDefinition_NewModelStillRequiresProviderCatalogAndDoesNotPersistOnFailure()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        SignForUpgradeTest(package);
        SetUpgradeConfiguration(package,
            new { key = "provider", type = "llmProvider", label = "Provider", required = true },
            new { key = "model", type = "llmModel", label = "Model", required = true });
        var provider = new LlmProviderProfile { Id = Guid.NewGuid(), Name = "Offline", IsEnabled = true, DefaultChatModel = "old-model" };
        var definition = SeedDefinition(db, package, ActivationMode.OnDemand);
        definition.Configuration!.SettingsJson = JsonSerializer.Serialize(new { provider = provider.Id.ToString("D"), model = "old-model" });
        var originalSettings = definition.Configuration.SettingsJson;
        db.Add(provider);
        await db.SaveChangesAsync();
        var catalog = new OfflineUpgradeModelCatalog();
        var service = new AgentInstallationConfigurationService(db, new TestAuditEventWriter(), modelCatalog: catalog);
        var newSettings = new Dictionary<string, JsonElement>
        {
            ["provider"] = JsonSerializer.SerializeToElement(provider.Id.ToString("D")),
            ["model"] = JsonSerializer.SerializeToElement("new-model")
        };

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() => service.SaveDefinitionAsync(
            definition.Id, new PutAgentDefinitionConfigurationRequest("1", newSettings, 1)));

        Assert.Contains("trusted provider model catalog could not be loaded", exception.Message);
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(originalSettings, definition.Configuration.SettingsJson);
        Assert.Equal(1, definition.Configuration.Revision);
    }

    private static void SetUpgradeConfiguration(AgentPackageVersion package, params object[] fields)
    {
        var manifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(package.ManifestJson)!;
        manifest["configuration"] = fields;
        package.ManifestJson = JsonSerializer.Serialize(manifest);
    }

    private static void SignForUpgradeTest(AgentPackageVersion package)
    {
        package.PackageDigest = $"sha256:{new string('a', 64)}";
        package.ArtifactSignature = "verified-signature";
    }

    private sealed class OfflineUpgradeModelCatalog : IModelCatalogClient
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("Connection refused (localhost:8888)");
        }
    }
}
