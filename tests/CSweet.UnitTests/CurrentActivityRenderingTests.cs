using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class CurrentActivityRenderingTests
{
    [Fact]
    public async Task CollapsedListKeepsLiveExecutionAheadOfExpiredAndWaitingRows()
    {
        await using var f = new Fixture();
        var row = f.Handler.Item;
        f.Handler.Items = [
            row with { Key = "expired", Title = "Expired work", LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            row with { Key = "waiting", Title = "Waiting work", State = "Waiting" },
            row with { Key = "stale", Title = "Stale work", StartedAt = DateTimeOffset.UtcNow.AddHours(-1), LastProgressAt = null },
            row with { Key = "running", Title = "Live execution" }];
        var html = await f.RenderAsync();
        Assert.True(html.IndexOf("Live execution", StringComparison.Ordinal) < html.IndexOf("Expired work", StringComparison.Ordinal));
        Assert.Contains("Waiting work", html);
        Assert.DoesNotContain("Stale work", html);
    }

    [Fact]
    public async Task PersonalTaskLinksUseBoardOwnerForCardAndPopover()
    {
        await using var f = new Fixture();
        var owner = Guid.NewGuid();
        f.Handler.Item = f.Handler.Item with { PersonalBoardOwnerId = owner, Category = "Project" };
        var html = await f.RenderAsync();
        var path = $"/employees/{owner}?tab=personal-board&amp;item={f.Handler.Item.WorkItemId}";
        Assert.Contains(path, html);
        Assert.DoesNotContain($"/work/boards/{f.Handler.Item.BoardId}", html);
        await f.InvokeAsync("OpenAsync", f.Handler.Item);
        html = await f.HtmlAsync();
        Assert.Equal(2, html.Split(path).Length - 1);
    }

    [Fact]
    public async Task CollaborationCardShowsBothAgentsActiveSpeakerAndLinkedTask()
    {
        await using var f = new Fixture();
        var partnerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        f.Handler.Item = f.Handler.Item with { ActivityKind = "Collaboration", EmployeeRole = "Engineer", ProjectId = projectId, CollaborationName = "First playable <plan>",
            Collaborator = new(partnerId, "Casey <script>", "Architect"), TechnicalName = "com.csweet.agent.coordination.turn-requested.v1" };
        var html = await f.RenderAsync();
        Assert.Contains("Collaboration: First playable &lt;plan&gt;", html);
        Assert.Contains($"/projects/{projectId}", html);
        Assert.Contains("Responding now", html);
        Assert.Contains("Casey &lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Architect", html);
        Assert.Contains($"/employees/{partnerId}", html);
        Assert.Contains($"/work/boards/{f.Handler.Item.BoardId}?item={f.Handler.Item.WorkItemId}", html);
        Assert.DoesNotContain("Technical details", html);
        Assert.DoesNotContain("Recent progress", html);
        Assert.DoesNotContain("com.csweet.agent.coordination.turn-requested.v1</code>", html);
        Assert.Contains("Peek at provider stream", html);
        Assert.Equal(2, html.Split($"/employees/{partnerId}").Length - 1); // Portrait and name.
        Assert.Equal(0, f.Handler.FeedRequests);
        if (Environment.GetEnvironmentVariable("CSWEET_ACTIVITY_CARD_PREVIEW") is string path) await File.WriteAllTextAsync(path, html);
    }

    [Fact]
    public async Task ExpiredCollaborationNeverMarksAgentActive()
    {
        await using var f = new Fixture();
        f.Handler.Item = f.Handler.Item with { ActivityKind = "Collaboration", LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var html = await f.RenderAsync();
        Assert.Contains("Recovering", html);
        Assert.DoesNotContain("Responding now", html);
        Assert.DoesNotContain("activity-agent-avatar active", html);
    }

    [Fact]
    public async Task QueuedReviewHasSpecificLayoutWithoutInventingTaskLinkOrActiveAgent()
    {
        await using var f = new Fixture();
        f.Handler.Item = f.Handler.Item with { ActivityKind = "Review", State = "Waiting", WorkItemId = null, BoardId = null,
            CurrentAction = "Queued while this employee handles another request" };
        var html = await f.RenderAsync();
        Assert.Contains("Review queued", html);
        Assert.Contains("What&#x27;s next", html);
        Assert.DoesNotContain("activity-task-link", html);
        Assert.DoesNotContain("Working now", html);
    }

    [Fact]
    public async Task CompactRowsShowExecutorWithoutOldDetailsAndLoadDiagnosticsOnlyWhenOpened()
    {
        await using var f = new Fixture();
        var html = await f.RenderAsync();
        Assert.Contains("Keyboard navigation", html); Assert.Contains("Morgan", html);
        Assert.DoesNotContain("Read criteria", html); Assert.DoesNotContain("Updated focus", html);
        Assert.DoesNotContain("data-activity-stream", html);
        Assert.Equal(0, f.Handler.FeedRequests);
        Assert.Contains("aria-pressed=\"true\"", html);
    }

    [Fact]
    public async Task PopoverOrdersFragmentsDeduplicatesReplayAndEncodesUntrustedText()
    {
        await using var f = new Fixture(); await f.RenderAsync();
        await f.InvokeAsync("OpenAsync", f.Handler.Item);
        await f.InvokeAsync("RefreshFeedAsync");
        var html = await f.HtmlAsync();
        Assert.Contains("Provider stream", html);
        Assert.Contains("data-activity-stream", html);
        Assert.Contains("first second &lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("first first", html);
        Assert.Contains("Follow latest", html);
        Assert.Contains("Open work item", html);
        if (Environment.GetEnvironmentVariable("CSWEET_ACTIVITY_PREVIEW") is string path) await File.WriteAllTextAsync(path, html);
    }

    [Fact]
    public async Task FeedAuthorizationRevocationClearsPreviouslyDisplayedEvidence()
    {
        await using var f = new Fixture(); await f.RenderAsync();
        await f.InvokeAsync("OpenAsync", f.Handler.Item);
        f.Handler.Forbidden = true;
        await f.InvokeAsync("RefreshFeedAsync");
        var html = await f.HtmlAsync();
        Assert.DoesNotContain("first second", html);
        Assert.Contains("access has changed", html);
    }

    [Fact]
    public async Task StreamToggleClosesAndUnauthorizedCardsDoNotFetchEvidence()
    {
        await using var f = new Fixture(); await f.RenderAsync();
        await f.InvokeAsync("ToggleStreamAsync", f.Handler.Item);
        Assert.Contains("first second", await f.HtmlAsync());
        await f.InvokeAsync("ToggleStreamAsync", f.Handler.Item);
        Assert.DoesNotContain("data-activity-stream", await f.HtmlAsync());
        var reads = f.Handler.FeedRequests;
        f.Handler.Item = f.Handler.Item with { CanInspect = false };
        await f.InvokeAsync("RefreshAsync");
        await f.InvokeAsync("ToggleStreamAsync", f.Handler.Item);
        Assert.Contains("do not have access", await f.HtmlAsync());
        Assert.Equal(reads, f.Handler.FeedRequests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly HtmlRenderer renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root;
        private CurrentActivity component = null!;
        public Handler Handler { get; } = new();
        public Fixture()
        {
            var registrations = new ServiceCollection().AddLogging(); registrations.AddMudServices();
            registrations.AddSingleton<IJSRuntime, NoJavaScript>(); registrations.AddSingleton<NavigationManager, Navigation>();
            registrations.AddSingleton(new HttpClient(Handler) { BaseAddress = new Uri("http://localhost") });
            registrations.AddScoped<AppRealtimeState>(); services = registrations.BuildServiceProvider();
            renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> RenderAsync() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = await renderer.RenderComponentAsync<CaptureActivity>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CurrentActivity.OrganizationId)] = Guid.NewGuid(), [nameof(CaptureActivity.Capture)] = (Action<CurrentActivity>)(value => component = value) }));
            return root.ToHtmlString();
        });
        public Task InvokeAsync(string method, params object[] values) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(CurrentActivity).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, values)!;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        });
        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); }
    }
    public sealed class CaptureActivity : CurrentActivity
    {
        [Parameter] public Action<CurrentActivity>? Capture { get; set; }
        protected override void OnInitialized() { Capture?.Invoke(this); base.OnInitialized(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public IReadOnlyList<CurrentActivityItem>? Items { get; set; }
        public int FeedRequests { get; private set; }
        public bool Forbidden { get; set; }
        private readonly Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        public CurrentActivityItem Item { get; set; } = new("task:1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            Guid.NewGuid(), "Morgan", "Keyboard navigation", "ENG-142", "Project", "Customer portal", "Executing", "Running checks",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3), "Test provider", "test-model", true,
            [new("a", "Progress", "Read criteria", DateTimeOffset.UtcNow.AddMinutes(-2)), new("b", "Progress", "Updated focus", DateTimeOffset.UtcNow.AddMinutes(-1))]);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/feed"))
            {
                FeedRequests++;
                if (Forbidden) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                result = new CurrentActivityFeedPage([
                    new(second, 1, "reasoning", "Reasoning", "Provider reasoning", "second <script>", DateTimeOffset.UtcNow, true, 2),
                    new(first, 2, "reasoning", "Reasoning", "Provider reasoning", "first ", DateTimeOffset.UtcNow, true, 1)], 2, false, false, false);
            }
            else result = new CurrentActivityPage(DateTimeOffset.UtcNow, Items?.Count ?? 1, Items ?? [Item], null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("http://localhost/", "http://localhost/"); protected override void NavigateToCore(string uri, bool forceLoad) { } }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
