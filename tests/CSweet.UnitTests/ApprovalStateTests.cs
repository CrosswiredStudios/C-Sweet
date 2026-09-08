using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.Core;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class ApprovalStateTests
{
    [Fact]
    public async Task CountsOnlyActionablePendingItemsAndClearsWhenCompanyChanges()
    {
        var businesses = new TestBusinessContext();
        using var http = new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost") };
        await using var realtime = new AppRealtimeState(http);
        using var state = new ApprovalState(http, businesses, realtime);
        var changes = 0;
        state.Changed += () => changes++;
        await state.InitializeAsync();
        Assert.Equal(1, state.PendingCount);
        Assert.Equal(3, state.Dashboard!.Items.Count);
        businesses.Clear();
        Assert.Equal(0, state.PendingCount);
        Assert.Null(state.Dashboard);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task RealtimeEventsRefreshOnlyTheSelectedCompanyAndStopAfterDisposal()
    {
        var businesses = new TestBusinessContext();
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        await using var realtime = new AppRealtimeState(http);
        using var state = new ApprovalState(http, businesses, realtime);
        await state.InitializeAsync();
        Assert.Equal(1, handler.Requests);
        Publish(Guid.NewGuid(), AppRealtimeEvents.ApprovalChanged);
        Publish(businesses.SelectedBusiness!.Id, AppRealtimeEvents.DocumentChanged);
        Assert.Equal(1, handler.Requests);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Changed += () => refreshed.TrySetResult();
        Publish(businesses.SelectedBusiness.Id, AppRealtimeEvents.ApprovalChanged);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handler.Requests);
        state.Dispose();
        Publish(businesses.SelectedBusiness.Id, AppRealtimeEvents.ApprovalChanged);
        Assert.Equal(2, handler.Requests);

        void Publish(Guid organizationId, string eventType) =>
            typeof(AppRealtimeState).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(realtime, [new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, eventType,
                    organizationId, "approvals", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }))]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var items = new[] { Item("Pending", true), Item("Pending", false), Item("Approved", true) };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApprovalDashboardResponse(Guid.NewGuid(), 2, items))
            });
        }
        private static ApprovalDashboardItemResponse Item(string status, bool canDecide) =>
            new(Guid.NewGuid(), "AgentAction", "Review", "Summary", status, "Agent", "Manager",
                DateTimeOffset.UtcNow, null, "/approvals", canDecide);
    }

    private sealed class TestBusinessContext : IBusinessContext
    {
        public OrganizationResponse? SelectedBusiness { get; private set; } =
            new(Guid.NewGuid(), "Company", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public IReadOnlyList<OrganizationResponse> Businesses => [];
        public bool IsLoading => false;
        public string? ErrorMessage => null;
        public event Action? Changed;
        public void Clear() { SelectedBusiness = null; Changed?.Invoke(); }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAsync(Guid businessId, bool navigate = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
