using System.Net.Http.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

public sealed class CommunicationUnreadState(HttpClient http, IBusinessContext businesses, AppRealtimeState realtime) : IDisposable
{
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private bool _initialized;

    public int TotalUnreadCount { get; private set; }
    public IReadOnlyDictionary<Guid, int> ChatUnreadCounts { get; private set; } = new Dictionary<Guid, int>();
    public event Action? Changed;

    /// <summary>
    /// Raised when a new message raised the signed-in user's unread total. Self-authored messages, agent-perspective
    /// traffic, and messages already read never contribute to unread, so they never raise this event.
    /// </summary>
    public event Action? UnreadMessagesArrived;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            _initialized = true;
            businesses.Changed += OnBusinessChanged;
            realtime.EventReceived += OnRealtimeEvent;
            realtime.Reconnected += OnReconnected;
        }
        await ReloadAsync(cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var organizationId = businesses.SelectedBusiness?.Id;
        if (!organizationId.HasValue)
        {
            Apply(new CommunicationUnreadSummaryResponse(0, new Dictionary<Guid, int>()));
            return;
        }
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var summary = await http.GetFromJsonAsync<CommunicationUnreadSummaryResponse>(
                $"api/organizations/{organizationId}/communications/hub/unread-summary", cancellationToken);
            if (summary is not null) Apply(summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (InvalidOperationException) { }
        finally { _reloadLock.Release(); }
    }

    public void Apply(CommunicationUnreadSummaryResponse summary)
    {
        TotalUnreadCount = summary.TotalUnreadCount;
        ChatUnreadCounts = summary.ChatUnreadCounts;
        Changed?.Invoke();
    }

    private async void OnBusinessChanged() => await ReloadAsync();
    private async void OnReconnected() => await ReloadAsync();
    private async void OnRealtimeEvent(AppRealtimeEventEnvelope envelope)
    {
        var organizationId = businesses.SelectedBusiness?.Id;
        if (envelope.OrganizationId != organizationId ||
            !envelope.EventType.StartsWith("com.csweet.communication.", StringComparison.Ordinal)) return;
        var unreadBefore = TotalUnreadCount;
        await ReloadAsync();
        if (envelope.EventType == CommunicationEvents.MessageCreated &&
            businesses.SelectedBusiness?.Id == organizationId &&
            TotalUnreadCount > unreadBefore)
            UnreadMessagesArrived?.Invoke();
    }

    public void Dispose()
    {
        if (!_initialized) return;
        businesses.Changed -= OnBusinessChanged;
        realtime.EventReceived -= OnRealtimeEvent;
        realtime.Reconnected -= OnReconnected;
        _reloadLock.Dispose();
    }
}
