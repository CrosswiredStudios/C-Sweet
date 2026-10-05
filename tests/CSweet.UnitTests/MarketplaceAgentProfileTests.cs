using System.Net;
using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class MarketplaceAgentProfileTests
{
    [Fact]
    public async Task ProfileShowsDeclaredPurposesScopesAndExactIdsWithoutExecutingMarkup()
    {
        var profile = new AgentCatalogProfileResponse("local:test", "1.0.0",
            [new() { Name = "executive.operations", Description = "Coordinate <script>unsafe</script> priorities" }],
            [new() { Name = "platform.organization.snapshot.read.v1", Scope = "organization", Purpose = "Inspect reporting lines" }],
            ["com.csweet.workforce.changed.v1"], [], new(), [], []);
        var html = await Render(new() { [nameof(MarketplaceAgentProfile.Profile)] = profile });
        Assert.Contains("What this agent can do", html);
        Assert.Contains("Access it will request", html);
        Assert.Contains("Business &amp; platform", html);
        Assert.Contains("Inspect reporting lines", html);
        Assert.Contains("organization", html);
        Assert.Contains("platform.organization.snapshot.read.v1", html);
        Assert.Contains("com.csweet.workforce.changed.v1", html);
        Assert.Contains("No web access requested", html);
        Assert.Contains("Viewing this profile grants no access", html);
        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("Open source / free", html);
    }

    [Fact]
    public async Task FailedLookupDoesNotPresentUnknownAccessAsAnEmptyRequest()
    {
        var html = await Render([]);
        Assert.Contains("Access details are unavailable", html);
        Assert.Contains("Try again", html);
        Assert.DoesNotContain("No platform grants requested", html);
        Assert.DoesNotContain("No web access requested", html);
    }

    [Fact]
    public async Task LookupUsesBusinessScopeAndEscapesTheCatalogReference()
    {
        var organizationId = Guid.NewGuid();
        var reference = "local:com.example:abc&injected=true";
        var handler = new ProfileHandler();
        await Render(new()
        {
            [nameof(MarketplaceAgentProfile.OrganizationId)] = organizationId,
            [nameof(MarketplaceAgentProfile.Agent)] = Agent() with { AgentReference = reference }
        }, handler);
        Assert.Equal($"/api/core/organizations/{organizationId:D}/agents/catalog-profile?agentReference={Uri.EscapeDataString(reference)}", handler.RequestUri!.PathAndQuery);
    }

    private static AvailableAgent Agent() => new("local:test", "com.example.agent", AgentCatalogSource.LocalDirectory, [],
        AgentAvailabilityState.AvailableToInstall, null, "Evelyn", "Coordinates business priorities.", "C-Sweet", "Leadership", [], [],
        ["executive.operations"], null, "USD", null, 0, null, null, .8m, "Local source", RoleName: "Chief of Staff");

    private static async Task<string> Render(Dictionary<string, object?> parameters, ProfileHandler? handler = null)
    {
        parameters.TryAdd(nameof(MarketplaceAgentProfile.Agent), Agent());
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient(handler ?? new ProfileHandler()) { BaseAddress = new("http://localhost") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MarketplaceAgentProfile>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class ProfileHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
