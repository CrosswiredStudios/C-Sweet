using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Components.Hiring;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class OverviewHiringCandidateTests
{
    [Fact]
    public async Task BlockedAutomaticHireShowsItsReasonAndOffersTheSharedReview()
    {
        const string reason = "'LLM provider' must reference an enabled LLM provider profile.";
        var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Creative Director", "Own creative direction", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { AutomaticHiringEnabled = true, AutomaticHiringBlockReason = reason, SuggestedBy = "Evelyn Brooks",
            SelectedCatalogAgentJson = """{"agentReference":"catalog:naomi","name":"Naomi Chen"}""" };
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new OperationHandler()) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<OverviewHiringCandidateCard>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["OrganizationId"] = Guid.NewGuid(), ["Recommendation"] = recommendation }))).ToHtmlString());
        Assert.Contains("Automatic hiring needs attention", html); Assert.Contains("enabled LLM provider profile", html);
        Assert.Contains("Review hiring", html); Assert.Contains("Choose this role manually", html);
        Assert.DoesNotContain("is handling this role", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAutomaticHiringRevealsBrowseInPlaceWithoutNavigating(bool succeeds)
    {
        var organization = Guid.NewGuid();
        var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Creative Director", "Own creative direction", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { AutomaticHiringEnabled = true, SuggestedBy = "Evelyn Brooks", RemainingHeadcount = 1, HiringUrl = "/marketplace?role=Creative%20Director&recommendationId=this-role" };
        var api = new ManualSelectionHandler(recommendation with { AutomaticHiringEnabled = false, ManualSelectionRequested = true }, succeeds);
        var activator = new CapturingActivator();
        var navigation = new TestNavigation();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OverviewHiringCandidateCard>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["OrganizationId"] = organization, ["Recommendation"] = recommendation })));
        var automatic = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Automatic hiring", automatic);
        Assert.Contains("Evelyn Brooks is handling this role", automatic);
        Assert.Contains("Choose this role manually", automatic);
        Assert.DoesNotContain("Browse Candidates", automatic);
        var stopButton = Assert.Single(activator.Buttons, x => x.OnClick.HasDelegate);
        await renderer.Dispatcher.InvokeAsync(() => stopButton.OnClick.InvokeAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        var manual = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        if (succeeds)
        {
            Assert.Contains("Automatic hiring is stopped for this role", manual);
            Assert.Contains("Browse Candidates", manual);
            Assert.DoesNotContain("Choose this role manually", manual);
            Assert.DoesNotContain("Interviewing agents", manual);
        }
        else
        {
            Assert.Contains("Choose this role manually", manual);
            Assert.Contains("Could not stop automatic hiring", manual);
            Assert.DoesNotContain("Browse Candidates", manual);
        }
        Assert.Equal($"/api/core/organizations/{organization:D}/hiring/recommendations/{recommendation.Id:D}/choose-manually", api.PostedPath);
        Assert.Equal(0, navigation.NavigationCalls);
    }

    [Theory]
    [InlineData(AgentHireOperationStatuses.Starting, true)]
    [InlineData(AgentHireOperationStatuses.Queued, true)]
    [InlineData(AgentHireOperationStatuses.Building, true)]
    [InlineData(AgentHireOperationStatuses.CompletingHire, true)]
    [InlineData(AgentHireOperationState.UnconfirmedStatus, true)]
    [InlineData(AgentHireOperationStatuses.AwaitingConfirmation, true)]
    [InlineData(AgentHireOperationStatuses.Failed, false)]
    public async Task MatchingHireDisablesActionsAndUpdatesWhenTheOperationChanges(string status, bool disabled)
    {
        var organization = Guid.NewGuid();
        var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Director", "Own direction", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            HiringUrl = "/marketplace",
            SelectedCatalogAgentJson = """{"agentReference":"catalog:naomi","name":"Naomi Chen"}"""
        };
        var operation = new AgentHireOperationResponse(Guid.NewGuid(), Guid.NewGuid(), organization, null,
            "Naomi Chen", "Naomi Chen", status, "Preparing Naomi", "Preparing the build environment",
            0, 0, null, null, false, null, null, DateTimeOffset.UtcNow) { RecommendationId = recommendation.Id };
        var handler = new OperationHandler { Operations = [operation with { OrganizationId = Guid.NewGuid() }, operation with { RecommendationId = Guid.NewGuid() }] };
        var activator = new CapturingActivator();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        var state = provider.GetRequiredService<AgentHireOperationState>();
        await state.RefreshAsync();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OverviewHiringCandidateCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
        { ["OrganizationId"] = organization, ["Recommendation"] = recommendation })));
        var hire = Assert.Single(activator.Buttons, x => x.Variant == Variant.Filled);
        var browse = Assert.Single(activator.Buttons, x => x.Href is not null);
        Assert.False(hire.Disabled);
        handler.Operations = [operation];
        await renderer.Dispatcher.InvokeAsync(() => state.RefreshAsync());
        Assert.Equal(disabled, hire.Disabled);
        Assert.Equal(disabled, browse.Disabled);
        var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Equal(disabled, html.Contains("Preparing the build environment"));
        if (disabled)
        {
            await renderer.Dispatcher.InvokeAsync(() => hire.OnClick.InvokeAsync(new MouseEventArgs()));
            Assert.Equal(0, handler.Posts);
        }
        handler.Operations = [];
        await renderer.Dispatcher.InvokeAsync(() => state.RefreshAsync());
        Assert.False(hire.Disabled);
        Assert.False(browse.Disabled);
    }

    private sealed class OperationHandler : HttpMessageHandler
    {
        public AgentHireOperationResponse[] Operations { get; set; } = [];
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post) Posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Operations) });
        }
    }

    [Theory]
    [InlineData("Evelyn Brooks", "Naomi Chen", "Video Game Team", "Evelyn Brooks", true)]
    [InlineData("Evelyn Brooks", "Naomi Chen", null, "Evelyn Brooks", true)]
    [InlineData("Evelyn Brooks", "Evelyn Brooks", "Video Game Team", "Evelyn Brooks", false)]
    [InlineData("Evelyn Brooks", null, null, "Evelyn Brooks", false)]
    [InlineData(null, "Naomi Chen", "Video Game Team", "Naomi Chen", false)]
    [InlineData("", "Naomi Chen", "Video Game Team", "Naomi Chen", false)]
    [InlineData(null, null, null, "Hiring suggestion", false)]
    public async Task AttributionShowsTheRecommenderAndSeparatelyIdentifiesTheRoleRequester(
        string? suggestedBy, string? requester, string? team, string expectedName, bool separateRequester)
    {
        var organization = Guid.NewGuid();
        var evelynInstallation = Guid.NewGuid(); var naomiInstallation = Guid.NewGuid();
        var evelyn = new OrganizationUserResponse(Guid.NewGuid(), organization, null, null, null, "Evelyn Brooks", null, 1, 0, DateTimeOffset.UtcNow)
            { AgentInstallationId = evelynInstallation };
        var naomi = evelyn with { Id = Guid.NewGuid(), DisplayName = "Naomi Chen", AgentInstallationId = naomiInstallation };
        var portraits = EmployeePortraits.Build([evelyn, naomi],
            [Installation(evelynInstallation, "Chief of Staff", "evelyn"), Installation(naomiInstallation, "Creative Director", "naomi")], []);
        var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Video Game Producer", "Own delivery", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            SuggestedBy = suggestedBy, RequestingEmployee = requester, RequestingTeam = team,
            RemainingHeadcount = 1, HiringUrl = "/marketplace?recommendationId=approved-plan"
        };
        var activator = new CapturingActivator();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        RenderFragment child = builder =>
        {
            builder.OpenComponent<OverviewHiringCandidateCard>(0);
            builder.AddAttribute(1, nameof(OverviewHiringCandidateCard.OrganizationId), organization);
            builder.AddAttribute(2, nameof(OverviewHiringCandidateCard.Recommendation), recommendation);
            builder.CloseComponent();
        };
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CascadingValue<EmployeePortraits>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = portraits, ["ChildContent"] = child })));
        var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        var attention = Assert.Single(activator.AttentionCards);
        Assert.Equal(expectedName, attention.Name);
        Assert.Contains($"Hiring from {expectedName}", html);
        if (expectedName == "Hiring suggestion") Assert.Null(attention.EmployeeId);
        else
        {
            var employee = expectedName == evelyn.DisplayName ? evelyn : naomi;
            Assert.Equal(employee.Id, attention.EmployeeId);
            var role = expectedName == evelyn.DisplayName ? "Chief of Staff" : "Creative Director";
            Assert.Equal(!separateRequester && team is not null ? team : role, attention.Context);
            Assert.Contains(portraits.Find(employee.Id)!, html);
            Assert.DoesNotContain(portraits.Find(employee.Id == evelyn.Id ? naomi.Id : evelyn.Id)!, html);
        }
        if (separateRequester)
        {
            Assert.Contains($"Role requested by {requester}", html);
            if (team is not null) Assert.Contains(team, html);
        }
        else Assert.DoesNotContain("Role requested by", html);
        Assert.Equal(recommendation.HiringUrl, Assert.Single(activator.Buttons, x => x.Href is not null).Href);
    }

    private static AgentInstallationResponse Installation(Guid id, string role, string image) =>
        JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"Id":"{{id}}","AgentId":"{{image}}","RoleName":"{{role}}","ImageUrl":"https://example.com/{{image}}.jpg"}""")!;

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
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
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
    [Fact]
    public async Task SelectionRefreshReplacesInterviewingWithTheCandidateAndClearsItForManualChoice()
    {
        var organization = Guid.NewGuid();
        var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Creative Director", "Own creative direction", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { RemainingHeadcount = 1, HiringUrl = "/marketplace?recommendationId=approved-plan" };
        var activator = new CapturingActivator();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        ParameterView Parameters(HiringRecommendationResponse value) => ParameterView.FromDictionary(new Dictionary<string, object?>
        { ["OrganizationId"] = organization, ["Recommendation"] = value });
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OverviewHiringCandidateCard>(Parameters(recommendation)));
        var interviewing = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Interviewing agents", interviewing);
        Assert.DoesNotContain("Hire Naomi Chen", interviewing);

        var unmatched = recommendation with { NoSuitableAgentFound = true,
            SelectionRationale = "No eligible candidate is available under your publisher preference." };
        await renderer.Dispatcher.InvokeAsync(() => ((IComponent)activator.CandidateCard!).SetParametersAsync(Parameters(unmatched)));
        var noMatch = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("No suitable agent found", noMatch);
        Assert.Contains("suitable agent for Creative Director within your hiring preferences", noMatch);
        Assert.Contains("Open Agent Marketplace", noMatch);
        Assert.DoesNotContain("Interviewing agents", noMatch);
        Assert.DoesNotContain("mud-progress-circular", noMatch);
        Assert.Equal(recommendation.HiringUrl, activator.Buttons.Last(x => x.Href is not null).Href);

        var selected = recommendation with
        {
            SelectedCatalogAgentJson = """{"agentReference":"catalog:naomi","name":"Naomi Chen","publisher":"C-Sweet"}""",
            SelectionRationale = "Matches the approved role."
        };
        await renderer.Dispatcher.InvokeAsync(() => ((IComponent)activator.CandidateCard!).SetParametersAsync(Parameters(selected)));
        var ready = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.DoesNotContain("Interviewing agents", ready);
        Assert.DoesNotContain("candidate-preparation", ready);
        Assert.Contains("Hire Naomi Chen", ready);
        Assert.Contains("Matches the approved role.", ready);

        var manual = recommendation with { SelectionRationale = "Choose the candidate for this approved role." };
        await renderer.Dispatcher.InvokeAsync(() => ((IComponent)activator.CandidateCard!).SetParametersAsync(Parameters(manual)));
        var choosing = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Candidate review", choosing);
        Assert.Contains(manual.SelectionRationale, choosing);
        Assert.DoesNotContain("Interviewing agents", choosing);
        Assert.DoesNotContain("Naomi Chen", choosing);
        Assert.Contains("Browse Candidates", choosing);
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public List<MudButton> Buttons { get; } = [];
        public List<AttentionRequestCard> AttentionCards { get; } = [];
        public AgentHireDialog? Dialog { get; private set; }
        public OverviewHiringCandidateCard? CandidateCard { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is MudButton button) Buttons.Add(button);
            if (component is AttentionRequestCard attention) AttentionCards.Add(attention);
            if (component is AgentHireDialog dialog) Dialog = dialog;
            if (component is OverviewHiringCandidateCard candidateCard) CandidateCard = candidateCard;
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
        public int NavigationCalls { get; private set; }
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { NavigationCalls++; }
    }
    private sealed class ManualSelectionHandler(HiringRecommendationResponse updated, bool succeeds) : HttpMessageHandler
    {
        public string? PostedPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            PostedPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(succeeds ? HttpStatusCode.OK : HttpStatusCode.Forbidden) { Content = JsonContent.Create(updated) });
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
