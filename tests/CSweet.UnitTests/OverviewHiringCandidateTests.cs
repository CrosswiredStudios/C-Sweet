using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Components.Hiring;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class OverviewHiringCandidateTests
{
    [Fact]
    public async Task HireOpensTheSharedReviewWithTheSelectedCandidateAndPlanContext()
    {
        var organization = Guid.NewGuid();
        var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Creative Director", "Own creative direction", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            RemainingHeadcount = 1,
            HiringUrl = "/marketplace?recommendationId=approved-plan",
            SelectedCatalogAgentJson = """{"agentReference":"catalog:naomi","name":"Naomi Chen","publisher":"C-Sweet","price":15,"currency":"USD","imageUrl":"https://example.com/naomi.jpg"}""",
            SelectionRationale = "Matches the approved role."
        };
        var api = new Handler();
        var activator = new CapturingActivator();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetApiClients();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OverviewHiringCandidateCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
        { ["OrganizationId"] = organization, ["Recommendation"] = recommendation })));
        var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("15 USD", html);
        Assert.Contains("candidate-rationale", html);
        var hire = Assert.Single(activator.Buttons, x => x.Variant == Variant.Filled);
        var browse = Assert.Single(activator.Buttons, x => x.Href is not null);
        Assert.Equal(recommendation.HiringUrl, browse.Href);
        await renderer.Dispatcher.InvokeAsync(() => hire.OnClick.InvokeAsync(new MouseEventArgs()));
        var preview = Assert.Single(api.Previews);
        Assert.Equal("catalog:naomi", preview.AgentReference);
        Assert.Equal(recommendation.Id, preview.RecommendationId);
        Assert.Equal("Creative Director", preview.RoleTitle);
        Assert.Equal("Naomi Chen", preview.EmployeeDisplayName);
        Assert.Equal(organization, activator.Dialog!.OrganizationId);
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public List<MudButton> Buttons { get; } = [];
        public AgentHireDialog? Dialog { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is MudButton button) Buttons.Add(button);
            if (component is AgentHireDialog dialog) Dialog = dialog;
            return component;
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<PreviewMarketplaceHireRequest> Previews { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                Previews.Add((await request.Content!.ReadFromJsonAsync<PreviewMarketplaceHireRequest>(token))!);
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { message = "Preview unavailable" }) };
            }
            object result = request.RequestUri!.AbsolutePath.EndsWith("/teams") ? new { teams = Array.Empty<object>() } : Array.Empty<object>();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
