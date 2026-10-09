using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

public sealed class NotificationSoundTests
{
    [Fact]
    public void Defaults_MapOnlyCommunicationMessagesToTheBundledSound()
    {
        var options = new NotificationSoundOptions();

        Assert.True(options.TryGetSound(NotificationSoundEvents.CommunicationMessageReceived, out var url));
        Assert.Equal(NotificationSoundOptions.DefaultMessageSound, url);
        Assert.False(options.TryGetSound(NotificationSoundEvents.ApprovalRequested, out _));
    }

    [Fact]
    public void Configuration_AddsOverridesAndSilencesEventSounds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CSweet:NotificationSounds:Volume"] = "0.25",
            ["CSweet:NotificationSounds:Sounds:approvals.requested"] = "sounds/approval.mp3",
            ["CSweet:NotificationSounds:Sounds:communications.message-received"] = ""
        }).Build();

        var options = NotificationSoundOptions.FromConfiguration(configuration);

        Assert.Equal(0.25, options.Volume);
        Assert.True(options.TryGetSound(NotificationSoundEvents.ApprovalRequested, out var approval));
        Assert.Equal("sounds/approval.mp3", approval);
        Assert.False(options.TryGetSound(NotificationSoundEvents.CommunicationMessageReceived, out _));
    }

    [Fact]
    public async Task Player_PlaysMappedSoundAtConfiguredVolumeAndThrottlesBursts()
    {
        var js = new RecordingJsRuntime();
        var player = new NotificationSoundPlayer(js, new NotificationSoundOptions { Volume = 3 });

        Assert.True(await player.PlayAsync(NotificationSoundEvents.CommunicationMessageReceived));
        Assert.False(await player.PlayAsync(NotificationSoundEvents.CommunicationMessageReceived));
        Assert.False(await player.PlayAsync(NotificationSoundEvents.ApprovalRequested));

        var play = Assert.Single(js.ModuleCalls);
        Assert.Equal("play", play.Identifier);
        Assert.Equal(NotificationSoundOptions.DefaultMessageSound, play.Args[0]);
        Assert.Equal(1d, play.Args[1]);
    }

    [Fact]
    public async Task Player_IsSilentWhenDisabledAndSwallowsBrowserFailures()
    {
        var disabled = new NotificationSoundPlayer(new RecordingJsRuntime(), new NotificationSoundOptions { Enabled = false });
        Assert.False(await disabled.PlayAsync(NotificationSoundEvents.CommunicationMessageReceived));

        var failing = new NotificationSoundPlayer(new RecordingJsRuntime { FailImport = true },
            new NotificationSoundOptions { MinimumIntervalMilliseconds = 0 });
        Assert.False(await failing.PlayAsync(NotificationSoundEvents.CommunicationMessageReceived));
    }

    [Fact]
    public async Task UnreadState_RaisesArrivalOnlyWhenACreatedMessageRaisesUnread()
    {
        var businesses = new TestBusinessContext();
        var handler = new UnreadHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        await using var realtime = new AppRealtimeState(http);
        using var state = new CommunicationUnreadState(http, businesses, realtime);
        await state.InitializeAsync();
        var arrivals = 0;
        state.UnreadMessagesArrived += () => arrivals++;

        handler.Total = 2;
        await PublishAsync(CommunicationEvents.MessageCreated);
        Assert.Equal(1, arrivals);

        await PublishAsync(CommunicationEvents.MessageCreated); // own message: unread unchanged
        handler.Total = 3;
        await PublishAsync(CommunicationEvents.MessageUpdated);
        handler.Total = 0;
        await PublishAsync(CommunicationEvents.MessageCreated);
        Assert.Equal(1, arrivals);

        async Task PublishAsync(string eventType)
        {
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() => reloaded.TrySetResult();
            state.Changed += OnChanged;
            typeof(AppRealtimeState).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(realtime, [new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, eventType,
                    businesses.SelectedBusiness!.Id, "communications", DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(new { }))]);
            await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            state.Changed -= OnChanged;
            await Task.Delay(100); // let the realtime handler finish its post-reload arrival check
        }
    }

    private sealed class UnreadHandler : HttpMessageHandler
    {
        public int Total { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CommunicationUnreadSummaryResponse(Total, new Dictionary<Guid, int>()))
            });
    }

    private sealed record JsCall(string Identifier, object?[] Args);

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public bool FailImport { get; init; }
        public List<JsCall> ModuleCalls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import") throw new InvalidOperationException(identifier);
            if (FailImport) throw new JSException("import failed");
            return ValueTask.FromResult((TValue)(object)new RecordingModule(this));
        }
    }

    private sealed class RecordingModule(RecordingJsRuntime runtime) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            runtime.ModuleCalls.Add(new JsCall(identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestBusinessContext : IBusinessContext
    {
        public OrganizationResponse? SelectedBusiness { get; } =
            new(Guid.NewGuid(), "Company", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public IReadOnlyList<OrganizationResponse> Businesses => [];
        public bool IsLoading => false;
        public string? ErrorMessage => null;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAsync(Guid businessId, bool navigate = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
