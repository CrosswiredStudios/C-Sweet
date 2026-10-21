using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.UI.Components.Hiring;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using ActionResponse = CSweet.Contracts.Communications.SuggestedUserActionResponse;
using Recommendation = CSweet.Contracts.Core.HiringRecommendationResponse;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class HiringSuggestionRenderingTests
{
    [Theory]
    [InlineData(true, "Pending", false, false)]
    [InlineData(false, "Pending", false, false)]
    [InlineData(true, "Completed", false, false)]
    [InlineData(false, "Pending", true, false)]
    [InlineData(true, "Pending", false, true)]
    [InlineData(false, "Pending", false, true)]
    [InlineData(false, "Pending", true, true)]
    public async Task CardShowsSelectedAgentAndContextWithSharedHireReview(bool selected, string status, bool noSuitableAgent, bool automatic)
    {
        var id = Guid.NewGuid();
        var agent = new AvailableAgent("creative", "creative", AgentCatalogSource.FirstPartyCatalog, [],
            AgentAvailabilityState.AvailableToInstall, null, "Alex Morgan", "Creative direction", "C-Sweet",
            "Management", [], [], [], 0, "USD", null, 0, null, null, 1, "FirstParty")
            { ImageUrl = "https://example.com/alex.jpg" };
        var recommendation = new Recommendation(id, null, "Creative Director", "Lead the studio", "Pending",
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            SelectedCatalogAgentJson = selected ? JsonSerializer.Serialize(agent, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null,
            SelectionRationale = "Specializes in game creative direction.", RequestingEmployee = "Evelyn Brooks",
            NoSuitableAgentFound = noSuitableAgent,
            AutomaticHiringEnabled = automatic,
            HiringUrl = $"/marketplace?role=Creative%20Director&recommendationId={id:D}"
        };
        var action = new ActionResponse(Guid.NewGuid(), "hiring.marketplace.browse.v1", "Browse candidates",
            null, $"/marketplace?recommendationId={id:D}", status, DateTimeOffset.UtcNow)
            { HiringRecommendationId = id, HiringRole = "Creative Director" };
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        var activator = new CapturingActivator();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(new HttpClient(new Handler(recommendation)) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<AgentHireOperationState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<HiringSuggestionCarousel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["OrganizationId"] = Guid.NewGuid(), ["Actions"] = new[] { action } }))).ToHtmlString());
        Assert.Equal(1, activator.HireDialogs);
        Assert.Contains("Creative Director", html);
        Assert.Contains("Plan proposed by Evelyn Brooks", html);
        if (noSuitableAgent)
        {
            Assert.Contains("No suitable agent found", html);
            Assert.Contains("suitable agent for Creative Director within your hiring preferences", html);
            Assert.Contains("role=Creative%20Director", html);
            Assert.DoesNotContain("Specializes in game creative direction.", html);
        }
        else Assert.Contains("Specializes in game creative direction.", html);
        if (selected)
        {
            Assert.Contains("Alex Morgan", html);
            Assert.Contains("https://example.com/alex.jpg", html);
            Assert.Contains("0 USD", html);
        }
        if (status == "Pending")
        {
            if (automatic)
            {
                Assert.Contains("Automatic hiring", html);
                Assert.Contains("Choose this role manually", html);
                Assert.DoesNotContain("Browse Candidates", html);
            }
            if (!automatic || noSuitableAgent)
            {
                Assert.Contains(noSuitableAgent ? "Open Agent Marketplace" : "Browse Candidates", html);
                Assert.Contains($"recommendationId={id:D}", html);
            }
            Assert.Equal(selected && !automatic, html.Contains(">Hire</span>", StringComparison.Ordinal));
        }
        else Assert.DoesNotContain("Browse Candidates", html);
    }

    private sealed class Handler(Recommendation recommendation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(recommendation) });
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public int HireDialogs { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            if (type == typeof(AgentHireDialog)) HireDialogs++;
            return (IComponent)Activator.CreateInstance(type)!;
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
