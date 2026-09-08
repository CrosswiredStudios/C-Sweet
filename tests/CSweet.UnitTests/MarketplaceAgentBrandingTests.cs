using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class MarketplaceAgentBrandingTests
{
    [Fact]
    public void ImporterRetainsOptionalBrandingAndAcceptsLegacyManifests()
    {
        var reader = new PluginManifestReader();
        var catalog = Catalog();
        catalog["imageUrl"] = "https://assets.example.com/avatar.webp";
        catalog["companyLogoUrl"] = "https://assets.example.com/logo.svg";
        catalog["accentColor"] = "#1C6252";
        catalog["longDescription"] = "Product direction, discovery, and a roadmap for the team.";
        var envelope = reader.Read(Manifest(catalog), "csweet-plugin.json");
        var result = JsonSerializer.Deserialize<PluginManifest>(envelope.ManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(catalog["imageUrl"]!.GetValue<string>(), result.Catalog.ImageUrl);
        Assert.Equal(catalog["companyLogoUrl"]!.GetValue<string>(), result.Catalog.CompanyLogoUrl);
        Assert.Equal("#1C6252", result.Catalog.AccentColor);
        Assert.Equal(catalog["longDescription"]!.GetValue<string>(), result.Catalog.LongDescription);
        Assert.NotNull(reader.Read(Manifest(Catalog()), "csweet-plugin.json"));
    }

    [Theory]
    [InlineData("imageUrl", "http://example.com/image.png")]
    [InlineData("companyLogoUrl", "javascript:alert(1)")]
    [InlineData("accentColor", "red; background: url(https://example.com)")]
    [InlineData("imageUrl", "https://user:secret@example.com/photo.png")]
    public void ImporterRejectsInvalidBranding(string field, string value)
    {
        var catalog = Catalog(); catalog[field] = value;
        var exception = Assert.Throws<JsonException>(() => new PluginManifestReader().Read(Manifest(catalog), "csweet-plugin.json"));
        Assert.Contains($"catalog.{field}", exception.Message);
    }

    [Fact]
    public void ImporterBoundsLongDescription()
    {
        var catalog = Catalog(); catalog["longDescription"] = new string('a', 2001);
        Assert.Throws<JsonException>(() => new PluginManifestReader().Read(Manifest(catalog), "csweet-plugin.json"));
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#FFFF00", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#1C6252", "#FFFFFF")]
    public void AccentChoosesReadableButtonText(string color, string foreground) =>
        Assert.Equal($"--agent-accent: {color}; --agent-accent-foreground: {foreground};", MarketplaceAgentPresentation.AccentStyle(color));

    [Fact]
    public async Task CardUsesDefaultArtworkAndCompanyNameWithoutBranding()
    {
        var html = await Render(Agent());
        Assert.Contains(MarketplaceAgentPresentation.DefaultImageUrl, html);
        Assert.Contains("Example Company", html);
        Assert.Contains("Short summary", html);
        Assert.Contains("Meet Agent", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("aria-hidden=\"true\"", html);
        Assert.Contains("inert", html);
    }

    [Fact]
    public async Task CardUsesConfiguredAssetsAndEscapesDescription()
    {
        var html = await Render(Agent() with
        {
            ImageUrl = "https://example.com/avatar.webp",
            CompanyLogoUrl = "https://example.com/company.svg",
            AccentColor = "#AABBCC",
            LongDescription = "Longer <script>not executable</script> description"
        });
        Assert.Contains("https://example.com/avatar.webp", html);
        Assert.Contains("https://example.com/company.svg", html);
        Assert.Contains("--agent-accent: #AABBCC", html);
        Assert.Contains("Short summary", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task RemoteUnsafeArtworkAndCssUseDefaults()
    {
        var html = await Render(Agent() with { ImageUrl = "javascript:alert(1)", CompanyLogoUrl = "file:///private", AccentColor = "red; --unsafe: 1" });
        Assert.Contains(MarketplaceAgentPresentation.DefaultImageUrl, html);
        Assert.Contains("Example Company", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("file:///", html);
        Assert.DoesNotContain("--unsafe", html);
        Assert.Contains(MarketplaceAgentPresentation.DefaultAccentColor, html);
    }

    [Theory]
    [InlineData("_content/CSweet.UI/images/agents/product-manager-v1.jpg", true)]
    [InlineData("_content/CSweet.UI/images/agents/chief-of-staff-v2.jpg", true)]
    [InlineData("https://example.com/portrait.webp", true)]
    [InlineData("_content/CSweet.UI/images/agents/../private.jpg", false)]
    [InlineData("_content/CSweet.UI/images/agents/%2e%2e/private-v1.jpg", false)]
    [InlineData("//example.com/portrait-v1.jpg", false)]
    [InlineData("file:///C:/portrait-v1.jpg", false)]
    [InlineData("_content/CSweet.UI/images/agents/chief-of-staff-v1.jpg?file=secret", false)]
    [InlineData("_content/CSweet.UI/images/agents/portrait-v1.jpg\n", false)]
    public void AgentArtworkAllowsOnlyHttpsOrBundledPortraitPaths(string path, bool expected) =>
        Assert.Equal(expected, MarketplaceAgentPresentation.IsAgentImageUrl(path));

    [Fact]
    public async Task CardRendersBundledPortraitWithoutRequiringTheMarketplace()
    {
        const string path = "_content/CSweet.UI/images/agents/product-manager-v1.jpg";
        var html = await Render(Agent() with { ImageUrl = path });
        Assert.Contains(path, html);
        Assert.DoesNotContain(MarketplaceAgentPresentation.DefaultImageUrl, html);
    }

    private static JsonObject Catalog() => new()
    {
        ["summary"] = "Short summary",
        ["role"] = new JsonObject { ["key"] = "product-manager", ["name"] = "Product Manager" },
        ["license"] = new JsonObject { ["spdxId"] = "MIT" }
    };
    private static byte[] Manifest(JsonObject catalog) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["manifestVersion"] = "2.0", ["kind"] = "agent", ["id"] = "com.example.agent", ["name"] = "Agent", ["version"] = "1.0.0",
        ["protocol"] = new JsonObject { ["minimumVersion"] = "2.0", ["maximumVersion"] = "2.x" }, ["catalog"] = catalog
    }.ToJsonString());
    private static AvailableAgent Agent() => new("local:test", "com.example.agent", AgentCatalogSource.LocalDirectory, [],
        AgentAvailabilityState.AvailableToInstall, null, "Maya", "Short summary", "Example Company", "Product", [], [], [],
        null, "USD", null, 0, null, null, .8m, "Local source", RoleName: "Product Manager");
    private static async Task<string> Render(AvailableAgent agent)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<MarketplaceAgentCard>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(MarketplaceAgentCard.Agent)] = agent }));
            return component.ToHtmlString();
        });
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
