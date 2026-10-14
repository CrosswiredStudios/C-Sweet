using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

/// <summary>Current actionable Overview items for the focused business, even away from Overview.</summary>
public sealed class OverviewAttentionState(HttpClient http, IBusinessContext businesses, AppRealtimeState realtime) : IDisposable
{
    private bool _initialized, _disposed, _loading, _refreshQueued;
    private long _generation;
    private CancellationTokenSource? _loadCancellation;
    private ApprovalDashboardResponse? _approvals;
    private HiringBacklogResponse? _hiring;
    private IReadOnlyList<PendingAgentQuestionResponse>? _questions;
    private ComputeDashboard? _compute;

    public Guid? OrganizationId { get; private set; }
    public int PendingCount => OverviewAttentionItems.Count(_approvals, _hiring, _questions, _compute);
    public bool HasMore => _compute is { CanManageNetwork: true, HasMore: true };
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        businesses.Changed += OnBusinessChanged;
        realtime.EventReceived += OnRealtimeEvent;
        realtime.Connected += OnConnectionChanged;
        realtime.Reconnected += OnConnectionChanged;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_disposed) return;
        var organizationId = businesses.SelectedBusiness?.Id;
        if (OrganizationId != organizationId)
        {
            ++_generation;
            _loadCancellation?.Cancel();
            _loading = false;
            _refreshQueued = false;
            OrganizationId = organizationId;
            _approvals = null; _hiring = null; _questions = null; _compute = null;
            Changed?.Invoke();
        }
        if (!organizationId.HasValue) return;
        if (_loading) { _refreshQueued = true; return; }
        _loading = true;
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        _loadCancellation?.Dispose();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            do
            {
                _refreshQueued = false;
                var prefix = $"api/core/organizations/{organizationId:D}";
                await Task.WhenAll(
                    ReadAsync<ApprovalDashboardResponse>($"{prefix}/approvals", x => _approvals = x),
                    ReadAsync<HiringBacklogResponse>($"{prefix}/dashboard/hiring", x => _hiring = x),
                    ReadAsync<List<PendingAgentQuestionResponse>>($"api/organizations/{organizationId:D}/communications/questions/pending", x => _questions = x),
                    ReadAsync<ComputeDashboard>($"api/organizations/{organizationId:D}/compute", x => _compute = x));
                if (!IsCurrent()) return;
                Changed?.Invoke();
            } while (_refreshQueued && IsCurrent());
        }
        finally
        {
            if (generation == _generation) _loading = false;
        }

        bool IsCurrent() => !_disposed && !token.IsCancellationRequested && generation == _generation &&
            businesses.SelectedBusiness?.Id == organizationId;

        async Task ReadAsync<T>(string uri, Action<T?> apply)
        {
            try
            {
                var result = await http.GetFromJsonAsync<T>(uri, token);
                if (IsCurrent()) apply(result);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                // Failed or unauthorized sources must not leave obsolete actionable items in the badge.
                if (IsCurrent()) apply(default);
            }
        }
    }

    private void OnBusinessChanged() => _ = ReloadAsync();
    private void OnConnectionChanged() => _ = ReloadAsync();
    private void OnRealtimeEvent(AppRealtimeEventEnvelope envelope)
    {
        if (envelope.OrganizationId == businesses.SelectedBusiness?.Id &&
            envelope.EventType is AppRealtimeEvents.ApprovalChanged or AppRealtimeEvents.HiringRecommendationsChanged or
                AppRealtimeEvents.ProjectIncidentChanged or AppRealtimeEvents.ComputeChanged or CommunicationEvents.DecisionUpdated)
            _ = ReloadAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        businesses.Changed -= OnBusinessChanged;
        realtime.EventReceived -= OnRealtimeEvent;
        realtime.Connected -= OnConnectionChanged;
        realtime.Reconnected -= OnConnectionChanged;
    }
}
