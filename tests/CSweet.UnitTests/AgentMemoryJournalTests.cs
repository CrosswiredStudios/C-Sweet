using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Memory;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class AgentMemoryJournalTests
{
    [Fact]
    public async Task EmbeddedJournalUsesRecordedHistoryAndRecallAndPreservesManagementTools()
    {
        using var api = new MemoryApi();
        var html = await RenderAsync(api);
        Assert.Contains("Learning journal", html);
        Assert.Contains("Reviewed by Matt", html);
        Assert.Contains("Recalled", html);
        Assert.Contains("Revision 2", html);
        Assert.Contains("Pinned context", html);
        Assert.Contains("Creative principles", html);
        Assert.Contains("Transfer knowledge", html);
        Assert.Contains("Processing & recovery", WebUtility.HtmlDecode(html));
        Assert.Contains("Saved erasure operations", html);
        Assert.DoesNotContain("Search and explore memory", html);
        Assert.All(api.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task UnavailableHistoryDoesNotHideRecordsOrInventLearningEvents()
    {
        var html = await RenderAsync(new MemoryApi(denyHistory: true));
        Assert.Contains("Prism Break direction", html);
        Assert.Contains("Some review or recall activity is unavailable", html);
        Assert.DoesNotContain("Reviewed by Matt", html);
    }

    [Fact]
    public async Task ConnectionsAreNamedAndInspectable()
    {
        var html = await RenderAsync(new MemoryApi(), "Connections");
        Assert.Contains("class=\"graph-node\"", html);
        Assert.Contains("Prism Break", html);
        Assert.Contains("leads", html);
        Assert.Contains("Memory relationships", html);
    }

    [Fact]
    public async Task SelectingARecordOpensItsReviewAndHistoryWithinTheTab()
    {
        var html = await RenderAsync(new MemoryApi(), "Inspector");
        Assert.Contains("has-selection", html);
        Assert.Contains("Close memory inspector", html);
        Assert.Contains("Check review access", html);
        Assert.Contains("Source & provenance", WebUtility.HtmlDecode(html));
        Assert.Contains("Version history", html);
    }

    [Fact]
    public async Task OldRouteReplacesHistoryWithTheEmployeeMemoryTabWithoutReadingData()
    {
        using var api = new MemoryApi();
        using var http = new HttpClient(api) { BaseAddress = new("http://test/") };
        var navigation = new TestNavigation();
        using var page = Create(http);
        typeof(AgentMemory).GetProperty("Navigation", Members)!.SetValue(page, navigation);
        await Call(page, "OnParametersSetAsync");
        Assert.EndsWith($"/employees/{Guid.Empty}?tab=memory", navigation.Target);
        Assert.True(navigation.Replace);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task PaginationKeepsAppliedFiltersAndDoesNotRepeatAlreadyLoadedActivity()
    {
        using var api = new MemoryApi();
        using var http = new HttpClient(api) { BaseAddress = new("http://test/") };
        using var page = Create(http);
        Set(page, "_search", "original");
        await Call(page, "LoadItemsAsync");
        var firstHistoryReads = api.Requests.Count(x => x.Contains("/history/"));
        Set(page, "_search", "unsent edit");
        await Call(page, "LoadMoreItemsAsync");
        Assert.Contains(api.Requests, x => x.Contains("search=original") && x.Contains("cursor=next"));
        Assert.DoesNotContain(api.Requests, x => x.Contains("unsent"));
        Assert.Equal(firstHistoryReads, api.Requests.Count(x => x.Contains("/history/")));
    }

    [Fact]
    public async Task PendingErasurePreventsSelectionAndSearchFromDiscardingItsReceipt()
    {
        using var http = new HttpClient(new MemoryApi()) { BaseAddress = new("http://test/") };
        using var page = Create(http);
        Set(page, "_selected", MemoryApi.Record);
        Set(page, "_erasureLocked", true);
        await Call(page, "SelectItemAsync", MemoryApi.Core);
        await Call(page, "LoadItemsAsync");
        typeof(AgentMemory).GetMethod("CloseInspector", Members)!.Invoke(page, null);
        Assert.Equal(MemoryApi.Record, Get(page, "_selected"));
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static AgentMemory Create(HttpClient http)
    {
        var page = new AgentMemory();
        typeof(AgentMemory).GetProperty("Http", Members)!.SetValue(page, http);
        return page;
    }
    private static void Set(AgentMemory page, string name, object value) => typeof(AgentMemory).GetField(name, Members)!.SetValue(page, value);
    private static object? Get(AgentMemory page, string name) => typeof(AgentMemory).GetField(name, Members)!.GetValue(page);
    private static Task Call(AgentMemory page, string name, params object[] args) => (Task)typeof(AgentMemory).GetMethod(name, Members)!.Invoke(page, args)!;

    private static async Task<string> RenderAsync(MemoryApi api, string view = "Journal")
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("http://test/") });
        var activator = new ActivatorForView(view == "Inspector" ? "Journal" : view);
        services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AgentMemory>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(AgentMemory.Embedded)] = true }));
            if (view == "Inspector")
            {
                await Call(activator.Page!, "SelectItemAsync", MemoryApi.Record);
                typeof(ComponentBase).GetMethod("StateHasChanged", Members)!.Invoke(activator.Page, null);
            }
            var html = component.ToHtmlString();
            if (!api.DenyHistory && Environment.GetEnvironmentVariable("CSWEET_MEMORY_PREVIEWS") is { } directory)
                await File.WriteAllTextAsync(Path.Combine(directory, $"memory-{view}.html"), html);
            return html;
        });
    }

    private sealed class ActivatorForView(string view) : IComponentActivator
    {
        public AgentMemory? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is AgentMemory page) { Page = page; Set(page, "_view", view); }
            return component;
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public string? Target { get; private set; }
        public bool Replace { get; private set; }
        public TestNavigation() => Initialize("http://test/", "http://test/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Target = uri;
        protected override void NavigateToCore(string uri, NavigationOptions options) { Target = uri; Replace = options.ReplaceHistoryEntry; }
    }
    private sealed class MemoryApi(bool denyHistory = false) : HttpMessageHandler
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        public static readonly AgentMemoryItemResponse Record = new(Guid.NewGuid(), "Claim", "Employee", null, null,
            "Prism Break direction", "Keep the creative brief focused on a playful, accessible arcade experience.", "enrichment", "Internal", "Confirmed", .9, At, null, null);
        public static readonly AgentMemoryItemResponse Core = new(Guid.NewGuid(), "Core", "Employee", null, null,
            "Creative principles", "Readable feedback, responsive controls, and a clear player promise.", "curated", "Internal", "Confirmed", null, At, null, new Dictionary<string, string> { ["pinned"] = "true" });
        public bool DenyHistory => denyHistory;
        public List<string> Requests { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri.PathAndQuery); Methods.Add(request.Method);
            object result;
            if (path.EndsWith("/summary")) result = new AgentMemorySummaryResponse(Guid.Empty, Guid.Empty, "Naomi Chen", Guid.Empty, "creative-director", "Creative Director", 16, 14, 17, 9, 3, At, 0, "Healthy");
            else if (path.EndsWith("/items")) result = request.RequestUri.Query.Contains("kind=Core") ? new AgentMemoryPageResponse([Core], null, 1) : new AgentMemoryPageResponse([Record], request.RequestUri.Query.Contains("cursor=") ? null : "next", 1);
            else if (path.EndsWith($"/items/{Record.Id}")) result = Record with { RecallUses = [new(Guid.NewGuid(), Guid.NewGuid(), "Claim", At.AddHours(2))] };
            else if (path.Contains("/history/"))
            {
                if (denyHistory) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                result = new MemoryHistoryPage("Claim", Record.Id, "Employee", [new(2, "corrected", At.AddHours(1), Record.Title, "A refined creative brief.", null, "Confirmed", null, "Internal", null, null, [], [], [new(Guid.NewGuid(), "Confirmed", Guid.NewGuid(), "Matt", At.AddMinutes(70), null)])], null);
            }
            else if (path.EndsWith("/graph")) result = new AgentMemoryGraphResponse([new(Record.Id, "Naomi Chen", "Person", "Employee", null), new(Core.Id, "Prism Break", "Project", "Employee", null)], [new(Guid.NewGuid(), Record.Id, Core.Id, "leads", .95)], false);
            else if (path.EndsWith("/enrichment/failures")) result = new MemoryEnrichmentJobPageResponse([], null);
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
