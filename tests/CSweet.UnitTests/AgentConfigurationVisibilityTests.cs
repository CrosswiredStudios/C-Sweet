using System.Text.Json;
using System.Runtime.CompilerServices;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentConfigurationVisibilityTests
{
    [Fact]
    public void UiVisibility_UsesExactControllerValueForManifestAndRuntimeFields()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profile"] = "general"
        };
        var manifestField = new PluginConfigurationField
        {
            Key = "description",
            VisibleWhenFieldKey = "profile",
            VisibleWhenValue = "custom"
        };
        var runtimeField = new AgentConfigurationField(
            "description", "Description", AgentConfigurationFieldTypes.TextArea, true,
            VisibleWhenFieldKey: "profile", VisibleWhenValue: "custom");

        Assert.False(AgentConfigurationVisibility.IsVisible(manifestField, values));
        Assert.False(AgentConfigurationVisibility.IsVisible(runtimeField, values));

        values["profile"] = "custom";
        Assert.True(AgentConfigurationVisibility.IsVisible(manifestField, values));
        Assert.True(AgentConfigurationVisibility.IsVisible(runtimeField, values));
    }

    [Fact]
    public async Task PlatformValidation_RequiresConditionalFieldOnlyWhenVisible()
    {
        await using var db = CreateDbContext();
        var manifest = new PluginManifest
        {
            Configuration =
            [
                new PluginConfigurationField
                {
                    Key = "profile", Label = "Profile", Type = "select", Required = true,
                    Options = [new("general", "General"), new("custom", "Custom")]
                },
                new PluginConfigurationField
                {
                    Key = "description", Label = "Description", Type = "textarea", Required = true,
                    VisibleWhenFieldKey = "profile", VisibleWhenValue = "custom"
                }
            ]
        };

        await AgentConfigurationRules.ValidateAsync(
            db, manifest, Settings(("profile", "general")), true, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            AgentConfigurationRules.ValidateAsync(
                db, manifest, Settings(("profile", "custom")), true, CancellationToken.None));
        Assert.Contains("Description", error.Message, StringComparison.Ordinal);

        await AgentConfigurationRules.ValidateAsync(
            db, manifest, Settings(("profile", "custom"), ("description", "A specialist studio")),
            true, CancellationToken.None);
    }

    [Fact]
    public void ConfigurationPages_UseSharedVisibilityEvaluation()
    {
        var root = GetRepositoryRoot();
        var relativePaths = new[]
        {
            "src/CSweet.UI/Pages/Organizations.razor",
            "src/CSweet.UI/Pages/Agents.razor",
            "src/CSweet.UI/Pages/Marketplace.razor",
            "src/CSweet.UI/Pages/Employees.razor.cs",
            "src/CSweet.UI/Components/HiringWorkflowApprovalCard.razor",
            "src/CSweet.UI/Pages/Plugins.razor",
            "src/CSweet.UI/Pages/PluginSetup.razor",
            "src/CSweet.UI/Setup/CommunicationsSetupStep.razor"
        };

        foreach (var relativePath in relativePaths)
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("AgentConfigurationVisibility.IsVisible", source, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> Settings(
        params (string Key, string Value)[] values) =>
        values.ToDictionary(
            value => value.Key,
            value => JsonSerializer.SerializeToElement(value.Value),
            StringComparer.Ordinal);

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CSweetDbContext(options);
    }
}
