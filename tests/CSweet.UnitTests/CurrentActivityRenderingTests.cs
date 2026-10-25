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
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class CurrentActivityRenderingTests
{
    [Theory]
    [InlineData(false, "42.3 tok/s", "Generation speed: 42.3 tokens per second")]
    [InlineData(true, "~42.3 tok/s", "Estimated generation speed: 42.3 tokens per second")]
    public async Task SpeedIsInlineWithTheCurrentActionWithoutOpeningTheStream(bool estimated, string display, string label)
    {
        await using var f = new Fixture();
        f.Handler.Item = f.Handler.Item with { CurrentAction = "Generating a response",
            Generation = new(Guid.NewGuid(), 42.3, estimated, DateTimeOffset.UtcNow) };
        await f.RenderAsync(); await f.SetConnectedAsync(true);
        var html = await f.HtmlAsync();
        Assert.Contains(display, html); Assert.Contains(label, html);
        var action = html.IndexOf("activity-card-action", StringComparison.Ordinal);
        var speed = html.IndexOf("activity-generation-speed", StringComparison.Ordinal);
        Assert.True(speed > action);
        Assert.True(speed > html.IndexOf("activity-card-summary", StringComparison.Ordinal));
        Assert.Equal(0, f.Handler.FeedRequests);
        Assert.DoesNotContain("data-activity-stream", html);
        await f.SetConnectedAsync(false);
        Assert.DoesNotContain("activity-generation-speed", await f.HtmlAsync());
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("waiting")]
    [InlineData("recovering")]
    [InlineData("unconfirmed")]
    [InlineData("no-access")]
    [InlineData("next-action")]
    public async Task SpeedIsHiddenWhenTheActionIsNoLongerGenerating(string reason)
    {
        await using var f = new Fixture();
        var row = f.Handler.Item with { Generation = new(Guid.NewGuid(), 42.3, true, DateTimeOffset.UtcNow) };
        row = reason switch
        {
            "stale" => row with { Generation = row.Generation! with { ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-16) } },
            "waiting" => row with { State = "Waiting" },
            "recovering" => row with { LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "unconfirmed" => row with { LastProgressAt = DateTimeOffset.UtcNow.AddMinutes(-6) },
            "no-access" => row with { CanInspect = false },
            _ => row with { Generation = null, CurrentAction = "Running tests" }
        };
        f.Handler.Item = row;
        await f.RenderAsync(); await f.SetConnectedAsync(true);
        Assert.DoesNotContain("activity-generation-speed", await f.HtmlAsync());
    }

    [Fact]
    public async Task SpeedNumbersFollowTheSelectedCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        var previousUi = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            await using var f = new Fixture();
            f.Handler.Item = f.Handler.Item with { Generation = new(Guid.NewGuid(), 42.3, true, DateTimeOffset.UtcNow) };
            await f.RenderAsync(); await f.SetConnectedAsync(true);
            Assert.Contains("42,3", await f.HtmlAsync());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
            System.Globalization.CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public async Task ComputeWaitShowsConfirmedProgressWithoutAnimatingTheWaitingAgent()
    {
        await using var fixture = new Fixture();
        fixture.Handler.Item = fixture.Handler.Item with { State = "Waiting", CurrentAction = "Compute is running; the agent is waiting for its result",
            AttemptId = null, LeaseExpiresAt = null, Compute = new("Browser testing", "Dispatching", 26,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(4), new('a', 40)) };
        var html = await fixture.RenderAsync();
        Assert.Contains("Compute progress", html);
        Assert.Contains("26 recent compute operations completed", html);
        Assert.Contains("not a QA verdict", html);
        Assert.Contains("Automatic resume eligible from", html);
        Assert.Contains("Last confirmed update", html);
        Assert.Contains("aaaaaaaa", html);
        Assert.DoesNotContain("activity-agent-avatar active", html);
        Assert.DoesNotContain("role=\"progressbar\"", html);
        Assert.Equal(0, fixture.Handler.FeedRequests);
    }

    [Theory]
    [InlineData("Waiting", "Waiting for an agent claim")]
    [InlineData("Executing", "Comparing proposed roles")]
    public async Task AgentAndStatusLeadTheCardWithLinkedTitleAndCurrentStepButNoDescription(string state, string action)
    {
        await using var f = new Fixture();
        f.Handler.Item = f.Handler.Item with { Title = "Review staffing <plan>",
            Description = "Compare engineering and QA coverage. <script>alert(1)</script>",
            ActivityKind = "Review", ActivityLabel = "Reviewing work", State = state, CurrentAction = action };
        var html = await f.RenderAsync();
        Assert.Contains("activity-title-link", html);
        Assert.Contains("Review staffing &lt;plan&gt;", html);
        Assert.DoesNotContain("Compare engineering and QA coverage.", html);
        Assert.DoesNotContain("activity-ticket-description", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<strong>Reviewing work</strong>", html);
        Assert.Contains(action, html);
        var agent = html.IndexOf("activity-card-agents", StringComparison.Ordinal);
        var status = html.IndexOf("activity-card-status", StringComparison.Ordinal);
        var title = html.IndexOf("activity-title-link", StringComparison.Ordinal);
        var step = html.IndexOf("activity-card-summary", StringComparison.Ordinal);
        Assert.True(agent >= 0 && agent < status && status < title && title < step);
        Assert.Contains("title=\"Review staffing &lt;plan&gt;\"", html);
        Assert.Contains("activity-title-text", html);
        Assert.Contains($"/projects/boards/{f.Handler.Item.BoardId}?item={f.Handler.Item.WorkItemId}", html);
        Assert.Equal(1, html.Split(">Customer portal<").Length - 1);
        Assert.Equal(0, f.Handler.FeedRequests);
        Assert.Equal(state == "Executing", html.Contains("activity-agent-avatar active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workspace_history_is_visible_without_opening_stream_and_is_html_encoded()
    {
        await using var f = new Fixture();
        f.Handler.Items = [f.Handler.Item with { PreviousSteps = [
            new("tool-1", "Workspace", "Tests failed (exit 1) <script>alert(1)</script>", DateTimeOffset.UtcNow),
            new("model-1", "Model", "Model call completed", DateTimeOffset.UtcNow)] }];
        var html = await f.RenderAsync();
        Assert.Contains("Recent workspace operations", html);
        Assert.Contains("Tests failed (exit 1)", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Equal(0, f.Handler.FeedRequests);
    }

    [Fact]
    public async Task FullListKeepsLiveExecutionAheadOfExpiredAndWaitingRows()
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
        Assert.Contains("Stale work", html);
        Assert.Equal(4, html.Split("data-activity-kind=").Length - 1);
        Assert.DoesNotContain("Show all", html);
        Assert.DoesNotContain("Show fewer", html);
        Assert.DoesNotContain("Load more work", html);
    }

    [Fact]
    public async Task FullListAutomaticallyReadsEveryPageAndDeduplicatesRows()
    {
        await using var f = new Fixture();
        var items = Enumerable.Range(0, 121).Select(i => f.Handler.Item with { Key = $"task:{i}", Title = $"Activity {i}" }).ToList();
        items.Insert(50, items[0]); // A row replayed across a page boundary.
        f.Handler.Items = items;
        var html = await f.RenderAsync();
        Assert.Equal(new[] { 0, 50, 100 }, f.Handler.ListOffsets);
        Assert.Equal(121, html.Split("data-activity-kind=").Length - 1);
        Assert.Contains("Activity 120", html);
        Assert.Equal(0, f.Handler.FeedRequests);
        Assert.DoesNotContain("Show all", html);
        Assert.DoesNotContain("Load more work", html);
    }

    [Fact]
    public async Task RefreshReplacesFullListWhenItShrinksAndGrowsAndClosesRemovedStream()
    {
        await using var f = new Fixture();
        var items = Enumerable.Range(0, 75).Select(i => f.Handler.Item with { Key = $"task:{i}", Title = $"Activity {i}" }).ToArray();
        f.Handler.Items = items;
        await f.RenderAsync();
        await f.InvokeAsync("OpenAsync", items[^1]);
        Assert.Contains("data-activity-stream", await f.HtmlAsync());
        f.Handler.Items = items.Take(2).ToArray();
        await f.InvokeAsync("RefreshAsync");
        var html = await f.HtmlAsync();
        Assert.Equal(2, html.Split("data-activity-kind=").Length - 1);
        Assert.DoesNotContain("data-activity-stream", html);
        f.Handler.Items = items;
        await f.InvokeAsync("RefreshAsync");
        Assert.Equal(75, (await f.HtmlAsync()).Split("data-activity-kind=").Length - 1);
        f.Handler.Items = [];
        await f.InvokeAsync("RefreshAsync");
        html = await f.HtmlAsync();
        Assert.Contains("No active agent work", html);
        Assert.Contains("activity-footer", html);
    }

    [Fact]
    public async Task ChangingFilterCancelsRemainingPagesWithoutDisplayingOldScope()
    {
        await using var f = new Fixture();
        f.Handler.Items = Enumerable.Range(0, 75).Select(i => f.Handler.Item with { Key = $"task:{i}", Title = $"Old scope {i}" }).ToArray();
        f.Handler.ProjectsItems = [f.Handler.Item with { Key = "project:1", Title = "Project scope" }];
        f.Handler.BlockedOffset = 50;
        var rendering = f.RenderAsync();
        await f.Handler.BlockedRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.InvokeAsync("FilterAsync", true);
        await rendering;
        var html = await f.HtmlAsync();
        Assert.Contains("Project scope", html);
        Assert.DoesNotContain("Old scope", html);
        Assert.Equal(1, html.Split("data-activity-kind=").Length - 1);
    }

    [Fact]
    public async Task PersonalTaskLinksUseBoardOwnerForCardAndPopover()
    {
        await using var f = new Fixture();
        var owner = Guid.NewGuid();
        f.Handler.Item = f.Handler.Item with { PersonalBoardOwnerId = owner, ProjectId = Guid.NewGuid(), Category = "Project" };
        var html = await f.RenderAsync();
        var path = $"/employees/{owner}?tab=personal-board&amp;item={f.Handler.Item.WorkItemId}";
        Assert.Contains(path, html);
        Assert.DoesNotContain($"/work/boards/{f.Handler.Item.BoardId}", html);
        await f.InvokeAsync("OpenAsync", f.Handler.Item);
        html = await f.HtmlAsync();
        Assert.Equal(2, html.Split(path).Length - 1);
    }

    [Fact]
    public async Task TaskWithoutProjectRetainsStandaloneBoardLink()
    {
        await using var f = new Fixture();
        f.Handler.Item = f.Handler.Item with { ProjectId = null };
        var html = await f.RenderAsync();
        Assert.Contains($"/projects/boards/{f.Handler.Item.BoardId}?item={f.Handler.Item.WorkItemId}", html);
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
        var taskPath = $"/projects/{projectId}?tab=work&amp;board={f.Handler.Item.BoardId}&amp;item={f.Handler.Item.WorkItemId}";
        Assert.Contains(taskPath, html);
        Assert.DoesNotContain("Technical details", html);
        Assert.DoesNotContain("Recent progress", html);
        Assert.DoesNotContain("com.csweet.agent.coordination.turn-requested.v1</code>", html);
        Assert.Contains("Peek at provider stream", html);
        Assert.Equal(2, html.Split($"/employees/{partnerId}").Length - 1); // Portrait and name.
        Assert.Equal(0, f.Handler.FeedRequests);
        await f.InvokeAsync("OpenAsync", f.Handler.Item);
        Assert.Equal(2, (await f.HtmlAsync()).Split(taskPath).Length - 1);
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
        Assert.Contains("What&#x27;s needed", html);
        Assert.DoesNotContain("activity-task-link", html);
        Assert.DoesNotContain("Working now", html);
    }

    [Fact]
    public async Task ReleaseReviewShowsActivityScopeAndRuntimeStatusWithoutOpeningStream()
    {
        await using var f = new Fixture();
        var projectId = Guid.NewGuid();
        f.Handler.Item = f.Handler.Item with { ActivityKind = "Review", ActivityLabel = "Technical review",
            Title = "Release: First playable <candidate>", EmployeeName = "Victor Lin", ProjectId = projectId,
            WorkItemId = null, CurrentAction = "Waiting for model response" };
        var html = await f.RenderAsync();
        Assert.Contains("Technical review", html);
        Assert.Contains("Reviewing now", html);
        Assert.Contains("Release: First playable &lt;candidate&gt;", html);
        Assert.Contains("Waiting for model response", html);
        Assert.Contains($"/projects/{projectId}", html);
        Assert.DoesNotContain("Background activity", html);
        Assert.DoesNotContain("activity-task-link", html);
        Assert.Equal(0, f.Handler.FeedRequests);
    }

    [Theory]
    [InlineData("Waiting", "Queued while this employee handles another request")]
    [InlineData("Recovering", "Execution lease expired; awaiting recovery")]
    [InlineData("Unconfirmed", "No task progress reported in the last five minutes")]
    [InlineData("Finalizing", "Agent finished; awaiting workflow result processing")]
    [InlineData("Needs attention", "Agent finished; workflow result processing is delayed")]
    public async Task SpecificActivityNeverImpliesExecutionWhenWaitingOrStale(string state, string action)
    {
        await using var f = new Fixture();
        f.Handler.Item = f.Handler.Item with { ActivityKind = "Review", ActivityLabel = "Technical review",
            WorkItemId = null, State = state, CurrentAction = action };
        var html = await f.RenderAsync();
        Assert.Contains("Technical review", html); Assert.Contains(action, html);
        Assert.DoesNotContain("Reviewing now", html);
        Assert.DoesNotContain("activity-agent-avatar active", html);
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
            var registrations = new ServiceCollection().AddLogging(); registrations.AddMudServices(); registrations.AddCSweetLocalization();
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
        public Task SetConnectedAsync(bool connected) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(CurrentActivity).GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, connected);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        });
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
        public IReadOnlyList<CurrentActivityItem>? ProjectsItems { get; set; }
        public List<int> ListOffsets { get; } = [];
        public int? BlockedOffset { get; set; }
        public TaskCompletionSource BlockedRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FeedRequests { get; private set; }
        public bool Forbidden { get; set; }
        private readonly Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        public CurrentActivityItem Item { get; set; } = new("task:1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            Guid.NewGuid(), "Morgan", "Keyboard navigation", "ENG-142", "Project", "Customer portal", "Executing", "Running checks",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3), "Test provider", "test-model", true,
            [new("a", "Progress", "Read criteria", DateTimeOffset.UtcNow.AddMinutes(-2)), new("b", "Progress", "Updated focus", DateTimeOffset.UtcNow.AddMinutes(-1))]);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/feed"))
            {
                FeedRequests++;
                if (Forbidden) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                result = new CurrentActivityFeedPage([
                    new(second, 1, "reasoning", "Reasoning", "Provider reasoning", "second <script>", DateTimeOffset.UtcNow, true, 2),
                    new(first, 2, "reasoning", "Reasoning", "Provider reasoning", "first ", DateTimeOffset.UtcNow, true, 1)], 2, false, false, false);
            }
            else
            {
                var offset = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&').Single(x => x.StartsWith("offset=", StringComparison.Ordinal))["offset=".Length..]);
                ListOffsets.Add(offset);
                if (BlockedOffset == offset)
                {
                    BlockedRead.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                var items = request.RequestUri.Query.Contains("projectsOnly=true", StringComparison.Ordinal) ? ProjectsItems ?? Items ?? [Item] : Items ?? [Item];
                var page = items.Skip(offset).Take(50).ToArray();
                result = new CurrentActivityPage(DateTimeOffset.UtcNow, items.Count, page, offset + page.Length < items.Count ? offset + page.Length : null);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("http://localhost/", "http://localhost/"); protected override void NavigateToCore(string uri, bool forceLoad) { } }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
