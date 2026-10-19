using System.Net;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Memory;
using CSweet.Contracts.Security;
using System.Net.Http.Json;
using CSweet.Contracts.Analytics;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Pages;
using CSweet.UI.Components.Employees.Models;
using System.Reflection;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class EmployeeDetailsPageTests
{
    [Fact]
    public async Task PersonalBoardTabParameterSelectsTheBoardTab()
    {
        var html = await RenderAsync("personal-board");

        Assert.Contains("mud-tab-panel-active", html);
        Assert.Contains("class=\"personal-board\"", html);
        Assert.DoesNotContain("Direct reports", html);
    }

    [Fact]
    public async Task MissingOrUnknownTabParameterFallsBackToOverview()
    {
        var missing = await RenderAsync(null);
        Assert.Contains("Direct reports", missing);
        Assert.DoesNotContain("class=\"personal-board\"", missing);

        var unknown = await RenderAsync("does-not-exist");
        Assert.Contains("Direct reports", unknown);
        Assert.DoesNotContain("class=\"personal-board\"", unknown);
    }

    [Theory]
    [InlineData("overview", "profile-facts")]
    [InlineData("personal-board", "personal-board")]
    [InlineData("assigned-work", "Open task")]
    [InlineData("configuration", "Working style")]
    [InlineData("agent-operations", "Runs & diagnostics")]
    [InlineData("memory", "Learning journal")]
    [InlineData("usage", "Model usage")]
    [InlineData("timeline", "timeline-feed")]
    public async Task EveryEmployeeTabRendersInTheSharedWorkspace(string tab, string expected)
    {
        var html = await RenderAsync(tab, new EmployeeApi(agent: true));
        Assert.Contains("aurora-page employee-workspace", html);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        if (tab == "assigned-work") Assert.Contains($"?item={EmployeeApi.ItemId}", html);
        if (Environment.GetEnvironmentVariable("CSWEET_EMPLOYEE_PREVIEWS") is string directory)
            await File.WriteAllTextAsync(Path.Combine(directory, $"employee-{tab}.html"), html);
    }

    [Fact]
    public async Task FailedUsageIsNotPresentedAsZero()
    {
        var html = await RenderAsync("usage", new EmployeeApi(failure: "usage"));
        Assert.Contains("Unavailable", html);
        Assert.Contains("Retry usage", html);
        Assert.DoesNotContain("0 model calls", html);
    }

    [Theory]
    [InlineData("personal-board", "personal-board", "Personal board could not be loaded")]
    [InlineData("assigned-work", "assignments", "Assigned work could not be loaded")]
    public async Task FailedBoardReadsOfferRetry(string tab, string endpoint, string expected)
    {
        var html = await RenderAsync(tab, new EmployeeApi(failure: endpoint));
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        Assert.Contains("Retry", html);
    }

    [Theory]
    [InlineData("usage")]
    [InlineData("memory")]
    [InlineData("agent-operations")]
    [InlineData("configuration")]
    public async Task RestrictedTabsDoNotRequestSensitiveData(string tab)
    {
        var api = new EmployeeApi(agent: true, sensitive: false);
        var html = await RenderAsync(tab, api);
        Assert.Contains("restricted", html);
        Assert.DoesNotContain(api.RequestedPaths, x => x.EndsWith("/usage") || x.EndsWith("/operations") || x.EndsWith("/summary") || x.Contains("/agent-configuration/"));
    }

    [Theory]
    [InlineData("Graph", "employee-graph-canvas")]
    [InlineData("Teams", "team-workspace")]
    [InlineData("Directory", "employee-directory-table")]
    [InlineData("Hiring", "No hiring recommendation yet")]
    public async Task EmployeeDirectoryViewsKeepTheirNavigation(string view, string expected)
    {
        var html = await RenderAsync(null, new EmployeeApi(), view);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        Assert.Contains("aurora-page employee-workspace", html);
        Assert.Contains("?tab=hiring", html);
        if (view == "Graph") Assert.Contains($"/employees/{EmployeeApi.EmployeeId}", html);
        if (Environment.GetEnvironmentVariable("CSWEET_EMPLOYEE_PREVIEWS") is string directory)
            await File.WriteAllTextAsync(Path.Combine(directory, $"employee-directory-{view}.html"), html);
    }

    private static async Task<string> RenderAsync(string? tab, EmployeeApi? api = null, string? directoryView = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api ?? new EmployeeApi()) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
        services.AddSingleton<IComponentActivator>(new PageActivator(tab, directoryView));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (directoryView is not null)
            {
                var directory = await renderer.RenderComponentAsync<Employees>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Employees.OrganizationId)] = EmployeeApi.OrganizationId }));
                return directory.ToHtmlString();
            }
            var component = await renderer.RenderComponentAsync<EmployeeDetails>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EmployeeDetails.OrganizationId)] = EmployeeApi.OrganizationId,
                [nameof(EmployeeDetails.EmployeeId)] = EmployeeApi.EmployeeId
            }));
            return component.ToHtmlString();
        });
    }

    // [SupplyParameterFromQuery] values arrive through framework-internal cascading suppliers that
    // static renderers do not register, so the query state is seeded at construction the same way
    // the router would seed it before the page's first render.
    private sealed class PageActivator(string? tab, string? directoryView) : IComponentActivator
    {
        public IComponent CreateInstance(Type componentType)
        {
            if (componentType == typeof(EmployeeDetails)) return new EmployeeDetails { Tab = tab };
            if (componentType == typeof(Employees))
            {
                var page = new Employees { Tab = directoryView?.ToLowerInvariant() };
                if (Enum.TryParse<EmployeeViewKind>(directoryView, out var view))
                    typeof(Employees).GetField("_activeView", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, view);
                return page;
            }
            return (IComponent)Activator.CreateInstance(componentType)!;
        }
    }

    private sealed class EmployeeApi(bool agent = false, bool sensitive = true, string? failure = null) : HttpMessageHandler
    {
        public static readonly Guid OrganizationId = Guid.NewGuid();
        public static readonly Guid EmployeeId = Guid.NewGuid();
        public List<string> RequestedPaths { get; } = [];
        public static readonly Guid InstallationId = Guid.NewGuid();
        public static readonly Guid ItemId = Guid.NewGuid();
        public static readonly Guid BoardId = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);
            if (failure is not null && path.EndsWith("/" + failure)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            object result;
            if (path.EndsWith("/details"))
                result = new EmployeeDetailsResponse(
                    new OrganizationUserResponse(EmployeeId, OrganizationId, null, null, null, "Ada Sterling", "ada@example.com", agent ? 1 : 0, 1, DateTimeOffset.UtcNow) { AgentInstallationId = agent ? InstallationId : null },
                    null, null, null, [], [],
                    new EmployeeDetailsPermissions(sensitive, true, true, true, true, false, true, true));
            else if (path.EndsWith("/agent-configuration/overrides"))
                result = EmployeeConfigurationPanelTests.SampleView();
            else if (path.EndsWith("/users"))
                result = new OrganizationUserResponse[] {
                    new(EmployeeId, OrganizationId, null, null, null, "Ada Sterling", "ada@example.com", 0, 1, DateTimeOffset.UtcNow),
                    new(Guid.NewGuid(), OrganizationId, EmployeeId, null, null, "Victor Lin", null, 1, 1, DateTimeOffset.UtcNow),
                    new(Guid.NewGuid(), OrganizationId, EmployeeId, null, null, "Naomi Chen", null, 1, 1, DateTimeOffset.UtcNow) };
            else if (path.EndsWith("/roles"))
                result = Array.Empty<RoleResponse>();
            else if (path.EndsWith("/personal-board"))
                result = new Wire.PersonalTodoBoard(BoardId, EmployeeId, "Ada Sterling", null, null, 1, []);
            else if (path.EndsWith("/assignments"))
                result = new EmployeeAssignedWorkResponse(EmployeeId, [new(
                    new Wire.WorkItemResponse(ItemId, OrganizationId, BoardId, Guid.NewGuid(), "Task", "Review the creative direction", "", "InProgress", "High", 1, 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Identifier = "PRISM-12" },
                    "Prism Break", "In progress", null, ["Assignee"], true)]);
            else if (path.EndsWith("/usage"))
                result = new InferenceAnalyticsResponse("24h", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new InferenceAnalyticsTotalsResponse(0, 0, 0, 0), []);
            else if (path.EndsWith("/runtime"))
                result = new AgentRuntimeReadinessResponse(InstallationId, null, "Ready", "Running", "Working on the creative direction.", null, null, null, true, false);
            else if (path.EndsWith("/runtime-history"))
                result = Array.Empty<AgentRuntimeRunResponse>();
            else if (path.EndsWith("/operations"))
                result = JsonSerializer.Deserialize<AgentInstallationResponse>("""
                    {"Id":"00000000-0000-0000-0000-000000000001", "GrantedCapabilities":["work.items.read","work.items.update","communications.send"], "GrantedNetworkAccess":["api.example.com"], "MemoryMb":512,"CpuPercent":50,"Schedule":{"ActivationMode":"OnDemand","MaxRuntimeSeconds":300}}
                    """);
            else if (path.EndsWith("/memory/summary"))
                result = new AgentMemorySummaryResponse(OrganizationId, EmployeeId, "Ada Sterling", InstallationId, "creative-director", "Creative Director", 24, 38, 12, 32, 4, DateTimeOffset.UtcNow, 0, "Healthy");
            else if (path.EndsWith("/memory/items")) result = new AgentMemoryPageResponse([], null, 0);
            else if (path.EndsWith("/memory/graph")) result = new AgentMemoryGraphResponse([], [], false);
            else if (path.EndsWith("/memory/enrichment/failures")) result = new MemoryEnrichmentJobPageResponse([], null);
            else if (path.EndsWith($"/organizations/{OrganizationId}"))
                result = new OrganizationResponse(OrganizationId, "Super Awesome Games", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            else if (path.EndsWith("/hiring"))
                result = JsonSerializer.Deserialize<HiringDashboardResponse>("""{"Recommendations":[],"ResourceChanges":[]} """);
            else if (path.EndsWith("/teams"))
                result = new TeamDirectoryResponse(OrganizationId, true, []);
            else if (path.EndsWith("/workers") || path.EndsWith("/installations") || path.EndsWith("/definitions") || path.EndsWith("/llm-provider-profiles"))
                result = Array.Empty<object>();
            else if (path.EndsWith("/timeline"))
                result = new SecurityEventPageResponse([], null);
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
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
