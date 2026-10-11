using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class AgentTokenBudgetTests
{
    [Theory]
    [InlineData(256000, 128000)]
    [InlineData(1000000, 500000)]
    [InlineData(10, 1)]
    public void BudgetHonorsConfiguredValuesWithoutHiddenCaps(int context, int output)
    {
        Assert.Equal(output, AgentTokenBudget.ResolveOutput(Settings(context, output)));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 101)]
    [InlineData(256000, 0)]
    [InlineData(0, 128000)]
    [InlineData(256000, 10.5)]
    public void InvalidBudgetFailsInsteadOfSilentlyClamping(double context, double output)
    {
        Assert.Throws<AgentInstallationException>(() => AgentTokenBudget.ResolveOutput(Settings(context, output)));
    }

    [Fact]
    public void LegacyLlmManifestExposesBothEditableDefaults()
    {
        var manifest = new PluginManifest
        {
            Requires = [new() { Name = CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream }],
            Configuration = [new() { Key = "maxOutputTokens", Maximum = 8192, Minimum = 2048 }]
        };
        var fields = AgentTokenBudget.Fields(manifest);
        Assert.Equal(2, fields.Count);
        Assert.All(fields, field => Assert.Null(field.Maximum));
        var defaults = AgentTokenBudget.WithDefaults(manifest, new Dictionary<string, JsonElement>());
        Assert.Equal(256000, defaults[AgentTokenBudget.ContextKey].GetInt32());
        Assert.Equal(128000, defaults[AgentTokenBudget.OutputKey].GetInt32());
        Assert.Equal(128000, AgentTokenBudget.ResolveOutput(defaults));
    }

    [Fact]
    public void NonLlmPluginKeepsItsConfiguration()
    {
        var manifest = new PluginManifest { Configuration = [new() { Key = "endpoint" }] };
        Assert.Single(AgentTokenBudget.Fields(manifest));
        Assert.Empty(AgentTokenBudget.WithDefaults(manifest, new Dictionary<string, JsonElement>()));
    }

    private static Dictionary<string, JsonElement> Settings(double context, double output) => new()
    {
        [AgentTokenBudget.ContextKey] = JsonSerializer.SerializeToElement(context),
        [AgentTokenBudget.OutputKey] = JsonSerializer.SerializeToElement(output)
    };
}
