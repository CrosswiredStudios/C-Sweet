using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.UI.Layout;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class OverviewAttentionStateTests
{
    [Fact]
    public async Task CountsAllFiveQueuesAndExcludesCompletedInaccessibleAndExpiredItems()
    {
        await using var fixture = new Fixture();
        await fixture.State.InitializeAsync();
        Assert.Equal(5, fixture.State.PendingCount);
        Assert.False(fixture.State.HasMore);
        fixture.Api.Compute = fixture.Api.Compute with { CanManageNetwork = false };
        await fixture.State.ReloadAsync();
        Assert.Equal(4, fixture.State.PendingCount);
        fixture.Businesses.Clear();
        Assert.Equal(0, fixture.State.PendingCount);
        Assert.Null(fixture.State.OrganizationId);
    }

    [Theory]
    [InlineData(AppRealtimeEvents.ApprovalChanged)]
    [InlineData(AppRealtimeEvents.HiringRecommendationsChanged)]
    [InlineData(AppRealtimeEvents.ProjectIncidentChanged)]
    [InlineData(AppRealtimeEvents.ComputeChanged)]
    [InlineData(CommunicationEvents.DecisionUpdated)]
    public async Task RealtimeChangesRefreshOnlyTheFocusedBusinessAndReconnectRecoversMissedChanges(string eventType)
    {
        await using var fixture = new Fixture();
        await fixture.State.InitializeAsync();
        fixture.Publish(Guid.NewGuid(), eventType);
        fixture.Publish(fixture.OrganizationId, AppRealtimeEvents.DocumentChanged);
        Assert.Equal(1, fixture.Api.HiringReads);
        fixture.Api.Hiring = new([]);
        fixture.Publish(fixture.OrganizationId, eventType);
        Assert.Equal(4, fixture.State.PendingCount);
        fixture.Api.Questions = [];
        fixture.Reconnect();
        Assert.Equal(3, fixture.State.PendingCount);
        var reads = fixture.Api.HiringReads;
        fixture.State.Dispose();
        fixture.Publish(fixture.OrganizationId, eventType);
        fixture.Reconnect();
        Assert.Equal(reads, fixture.Api.HiringReads);
    }

    [Fact]
    public async Task EventDuringLoadingQueuesAnotherReadAndDoesNotLoseNewItems()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.FirstHiringRead = async () => { entered.SetResult(); await release.Task; };
        var load = fixture.State.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Api.Hiring = new([]);
        fixture.Publish(fixture.OrganizationId, AppRealtimeEvents.HiringRecommendationsChanged);
        fixture.Publish(fixture.OrganizationId, AppRealtimeEvents.HiringRecommendationsChanged);
        release.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Api.HiringReads);
        Assert.Equal(4, fixture.State.PendingCount);
    }

    [Fact]
    public async Task BusinessSwitchRejectsLateResultsAndClearsPreviousBusinessCountImmediately()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.FirstHiringRead = async () => { entered.SetResult(); await release.Task; };
        var load = fixture.State.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Api.Empty = true;
        var second = Guid.NewGuid();
        await fixture.Businesses.SelectAsync(second);
        Assert.Equal(second, fixture.State.OrganizationId);
        Assert.Equal(0, fixture.State.PendingCount);
        release.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.State.PendingCount);
        Assert.True(fixture.Api.FirstToken.IsCancellationRequested);
    }

    [Fact]
    public async Task UnavailableSourceClearsOnlyItsItemsAndRecoversOnNextRead()
    {
        await using var fixture = new Fixture();
        await fixture.State.InitializeAsync();
        fixture.Api.HiringStatus = HttpStatusCode.Forbidden;
        await fixture.State.ReloadAsync();
        Assert.Equal(4, fixture.State.PendingCount);
        fixture.Api.HiringStatus = HttpStatusCode.OK;
        await fixture.State.ReloadAsync();
        Assert.Equal(5, fixture.State.PendingCount);
    }

    [Theory]
    [InlineData(0, false, null)]
    [InlineData(1, false, "1 item needs your attention")]
    [InlineData(6, false, "6 items need your attention")]
    [InlineData(120, false, "120 items need your attention")]
    [InlineData(6, true, "At least 6 items need your attention")]
    public async Task SidebarBadgeShowsTheCountAndHidesAtZero(int count, bool hasMore, string? label)
    {
        var api = new Handler { Empty = count == 0 };
        api.Questions = Enumerable.Range(0, Math.Max(0, count - 4)).Select(_ => Question("Pending")).ToList();
        if (count == 1)
        {
            api.Approvals = new(Guid.NewGuid(), 0, []);
            api.Hiring = new([]);
            api.Compute = api.Compute with { Resources = [] };
            api.Questions = [Question("Pending")];
        }
        api.Compute = api.Compute with { HasMore = hasMore };
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetApiClients();
        services.AddSingleton<IBusinessContext>(new TestBusinessContext());
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<NavMenu>()).ToHtmlString());
        html = WebUtility.HtmlDecode(html);
        if (label is null) Assert.DoesNotContain("needs your attention", html);
        else
        {
            Assert.Contains($"aria-label=\"{label}\"", html);
            Assert.Contains($">{(count > 99 ? "99+" : $"{count}{(hasMore ? "+" : "")}")}</span>", html);
        }
    }

    private static PendingAgentQuestionResponse Question(string status) => new(Guid.NewGuid(), Guid.NewGuid(), "Producer",
        new(Guid.NewGuid(), "Direction?", status, [], "", null, null, DateTimeOffset.UtcNow, null));

    private sealed class Fixture : IAsyncDisposable
    {
        public Handler Api { get; } = new();
        public TestBusinessContext Businesses { get; } = new();
        private readonly HttpClient _http;
        private readonly AppRealtimeState _realtime;
        public OverviewAttentionState State { get; }
        public Guid OrganizationId => Businesses.SelectedBusiness!.Id;
        public Fixture()
        {
            _http = new(Api) { BaseAddress = new Uri("http://localhost/") };
            _realtime = new(_http);
            State = new(_http, Businesses, _realtime);
        }
        public void Publish(Guid org, string type) => typeof(AppRealtimeState)
            .GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_realtime,
                [new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, type, org, "attention", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }))]);
        public void Reconnect() => ((Action?)typeof(AppRealtimeState)
            .GetField("Reconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_realtime))?.Invoke();
        public async ValueTask DisposeAsync() { State.Dispose(); await _realtime.DisposeAsync(); _http.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public int HiringReads { get; private set; }
        public bool Empty { get; set; }
        public HttpStatusCode HiringStatus { get; set; } = HttpStatusCode.OK;
        public Func<Task>? FirstHiringRead { get; set; }
        public CancellationToken FirstToken { get; private set; }
        public ApprovalDashboardResponse Approvals { get; set; } = new(Guid.NewGuid(), 99,
            [Approval("Pending", true), Approval("Pending", false), Approval("Approved", true)])
        {
            ProjectIncidents = [new(Guid.NewGuid(), Guid.NewGuid(), "Project", "Lead", "Stall", "Facts", "Review", "/report", Now)]
        };
        public HiringBacklogResponse Hiring { get; set; } = new([Hire("Pending", 1), Hire("Pending", 0), Hire("Completed", 1)]);
        public List<PendingAgentQuestionResponse> Questions { get; set; } = [Question("Pending"), Question("Answered")];
        public ComputeDashboard Compute { get; set; } = new(Now, null,
            [Resource(false, "Ready", "Running", Now.AddHours(1)), Resource(true, "Ready", "Running", Now.AddHours(1)),
             Resource(false, "Destroyed", "Running", Now.AddHours(1)), Resource(false, "Destroying", "Running", Now.AddHours(1)),
             Resource(false, "Ready", "Destroyed", Now.AddHours(1)), Resource(false, "Ready", "Running", Now)], false, true);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            object data;
            if (path.EndsWith("/dashboard/hiring"))
            {
                // Capture before waiting to emulate a late response from an old snapshot.
                data = Empty ? new HiringBacklogResponse([]) : Hiring;
                if (++HiringReads == 1) { FirstToken = token; if (FirstHiringRead is not null) await FirstHiringRead(); }
                if (HiringStatus != HttpStatusCode.OK) return new(HiringStatus);
            }
            else if (path.EndsWith("/approvals")) data = Empty ? new ApprovalDashboardResponse(Guid.NewGuid(), 0, []) : Approvals;
            else if (path.EndsWith("/questions/pending")) data = Empty ? new List<PendingAgentQuestionResponse>() : Questions;
            else if (path.EndsWith("/compute")) data = Empty ? new ComputeDashboard(Now, null, [], false) : Compute;
            else if (path.EndsWith("/unread-summary")) data = new CommunicationUnreadSummaryResponse(0, new Dictionary<Guid, int>());
            else data = Array.Empty<object>();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(data, data.GetType()) };
        }
        private static ApprovalDashboardItemResponse Approval(string status, bool canDecide) =>
            new(Guid.NewGuid(), "ResourceChange", "Review", "Summary", status, "Agent", "Owner", Now, null, "/approvals", canDecide);
        private static HiringRecommendationResponse Hire(string status, int remaining) =>
            new(Guid.NewGuid(), null, "Developer", "Objective", status, null, [], Now, Now) { RemainingHeadcount = remaining };
        private static ComputeResourceStatus Resource(bool allowed, string state, string desired, DateTimeOffset expiry) =>
            new(Guid.NewGuid(), "Agent", "Project", state, desired, "linux", 2, 2048, 20480, Now, expiry, null, allowed) { LocalLinkRequested = true };
    }

    private sealed class TestBusinessContext : IBusinessContext
    {
        public OrganizationResponse? SelectedBusiness { get; private set; } = Business(Guid.NewGuid());
        public IReadOnlyList<OrganizationResponse> Businesses => [];
        public bool IsLoading => false;
        public string? ErrorMessage => null;
        public event Action? Changed;
        public void Clear() { SelectedBusiness = null; Changed?.Invoke(); }
        public Task SelectAsync(Guid id, bool navigate = true, CancellationToken cancellationToken = default)
        { SelectedBusiness = Business(id); Changed?.Invoke(); return Task.CompletedTask; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        private static OrganizationResponse Business(Guid id) => new(id, "Company", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/projects");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
