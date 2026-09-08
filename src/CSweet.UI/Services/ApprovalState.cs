using System.Net.Http.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

public sealed class ApprovalState(HttpClient http, IBusinessContext businesses, AppRealtimeState realtime) : IDisposable
{
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    public Guid? OrganizationId { get; private set; }
    public ApprovalDashboardResponse? Dashboard { get; private set; }
    public int PendingCount => Dashboard?.Items.Count(x => x.Status == "Pending" && x.CanDecide) ?? 0;
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        businesses.Changed += OnBusinessChanged;
        realtime.EventReceived += OnRealtimeEvent;
        realtime.Connected += OnConnectionChanged;
        realtime.Reconnected += OnConnectionChanged;
        await ReloadAsync();
    }

    public async Task<ApprovalDashboardResponse?> LoadAsync(Guid organizationId)
    {
        await _reloadLock.WaitAsync();
        try
        {
            var dashboard = await http.GetFromJsonAsync<ApprovalDashboardResponse>(
                $"api/core/organizations/{organizationId:D}/approvals");
            if (!_disposed && businesses.SelectedBusiness?.Id == organizationId)
            {
                OrganizationId = organizationId;
                Dashboard = dashboard;
                Changed?.Invoke();
            }
            return dashboard;
        }
        finally { _reloadLock.Release(); }
    }

    public async Task ReloadAsync()
    {
        if (_disposed) return;
        var organizationId = businesses.SelectedBusiness?.Id;
        if (OrganizationId != organizationId)
        {
            OrganizationId = organizationId;
            Dashboard = null;
            Changed?.Invoke();
        }
        if (!organizationId.HasValue) return;
        try { await LoadAsync(organizationId.Value); }
        catch (HttpRequestException)
        {
            if (!_disposed && OrganizationId == organizationId)
            {
                Dashboard = null;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private void OnBusinessChanged() => _ = ReloadAsync();
    private void OnConnectionChanged() => _ = ReloadAsync();
    private void OnRealtimeEvent(AppRealtimeEventEnvelope envelope)
    {
        if (envelope.OrganizationId == businesses.SelectedBusiness?.Id &&
            envelope.EventType == AppRealtimeEvents.ApprovalChanged)
            _ = ReloadAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        businesses.Changed -= OnBusinessChanged;
        realtime.EventReceived -= OnRealtimeEvent;
        realtime.Connected -= OnConnectionChanged;
        realtime.Reconnected -= OnConnectionChanged;
    }
}
