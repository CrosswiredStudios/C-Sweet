using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace CSweet.UI.Components;

public partial class CurrentActivity
{
    [Parameter] public Guid OrganizationId { get; set; }
    [Parameter] public int RefreshVersion { get; set; }
    private (Guid, int) _scope;
    private CurrentActivityPage? _page;
    private readonly List<CurrentActivityItem> _rows = [];
    private CurrentActivityItem? _selected;
    private readonly List<CurrentActivityFeedEntry> _feed = [];
    private readonly HashSet<(Guid, string)> _seen = [];
    private CancellationTokenSource _requests = new(), _feedRequests = new();
    private bool _projectsOnly, _expanded, _loading, _disposed, _connected, _refreshQueued, _pumping;
    private bool _wide, _follow = true, _feedLoading, _feedHasMore, _feedOmitted, _evidenceUnavailable;
    private string? _error, _feedError;
    private long _sequence;
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<CurrentActivity>? _reference;
    private PeriodicTimer? _clock;

    private IEnumerable<CurrentActivityItem> VisibleRows => _expanded ? _rows : _rows.Take(3);
    private IEnumerable<CurrentActivityFeedEntry> FeedRows => _feed.GroupBy(x => x.Key).Select(group =>
    {
        var first = group.OrderBy(x => x.Sequence).First();
        if (!first.Append) return group.MaxBy(x => x.Sequence)!;
        var text = string.Concat(group.OrderBy(x => x.StreamSequence ?? x.Sequence).ThenBy(x => x.Sequence).Select(x => x.Text));
        return first with { Text = text.Length > 32000 ? "[Earlier text omitted]\n" + text[^32000..] : text };
    }).OrderBy(x => x.OccurredAt).ThenBy(x => x.Sequence);

    protected override void OnInitialized()
    {
        Realtime.EventReceived += OnEvent;
        Realtime.Connected += OnConnect;
        Realtime.Reconnected += OnConnect;
        Realtime.Disconnected += OnDisconnect;
        _connected = Realtime.IsConnected;
        _clock = new(TimeSpan.FromSeconds(15));
        _ = UpdateClockAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_scope == (OrganizationId, RefreshVersion)) return;
        var changedOrganization = _scope.Item1 != OrganizationId;
        _scope = (OrganizationId, RefreshVersion);
        if (changedOrganization) { ResetRequests(); _page = null; _rows.Clear(); CloseDetails(); }
        await RefreshAsync();
    }

    private async Task FilterAsync(bool projects)
    {
        if (_projectsOnly == projects) return;
        ResetRequests(); CloseDetails(); _projectsOnly = projects; _rows.Clear(); _page = null;
        await RefreshAsync();
    }

    private void ResetRequests() { _requests.Cancel(); _requests.Dispose(); _requests = new(); _loading = false; }
    private async Task RefreshAsync()
    {
        if (_loading || _disposed) { _refreshQueued = true; return; }
        var token = _requests.Token;
        _loading = true; _error = null;
        try
        {
            var target = Math.Max(50, _rows.Count);
            var items = new List<CurrentActivityItem>();
            CurrentActivityPage? page;
            var offset = 0;
            do
            {
                page = await Http.GetFromJsonAsync<CurrentActivityPage>(ListUrl(offset), token) ?? throw new HttpRequestException("Empty activity response.");
                if (token.IsCancellationRequested) return;
                items.AddRange(page.Items);
                offset = page.NextOffset ?? 0;
            } while (offset > 0 && items.Count < target);
            _page = page; _rows.Clear(); _rows.AddRange(items.DistinctBy(x => x.Key));
            if (_selected is { } selected)
            {
                var updated = _rows.FirstOrDefault(x => x.Key == selected.Key);
                if (updated is not null)
                {
                    if (updated.AttemptId != selected.AttemptId || updated.WorkItemId != selected.WorkItemId || updated.ModelRunId != selected.ModelRunId) await OpenAsync(updated);
                    else { _selected = updated; if (!updated.CanInspect) ResetFeed(); }
                }
                else if (page.NextOffset is null)
                    _selected = selected with { State = "No longer active", CurrentAction = "This work is no longer in the current activity list." };
                // Keep an open completed attempt inspectable; every feed read reauthorizes access.
                await RefreshFeedAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            if (!token.IsCancellationRequested)
            {
                _error = error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
                    ? "You no longer have access to this activity." : "Activity could not be refreshed. Displayed information may be stale.";
                if (error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }) { _page = null; _rows.Clear(); CloseDetails(); }
            }
        }
        finally { if (!token.IsCancellationRequested) _loading = false; }
    }

    private string ListUrl(int offset) => $"api/core/organizations/{OrganizationId}/dashboard/activity?offset={offset}&projectsOnly={_projectsOnly.ToString().ToLowerInvariant()}";
    private async Task LoadMoreAsync()
    {
        if (_loading || _page?.NextOffset is not int offset) return;
        var token = _requests.Token; _loading = true;
        try
        {
            var page = await Http.GetFromJsonAsync<CurrentActivityPage>(ListUrl(offset), token);
            if (page is not null && !token.IsCancellationRequested) { _page = page; foreach (var item in page.Items) if (_rows.All(x => x.Key != item.Key)) _rows.Add(item); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or JsonException) { if (!token.IsCancellationRequested) _error = "More activity could not be loaded. Refresh to try again."; }
        finally { if (!token.IsCancellationRequested) _loading = false; }
    }

    private async Task OpenAsync(CurrentActivityItem item) { ResetFeed(); _selected = item; _wide = false; _follow = true; await RefreshFeedAsync(); }
    private void ResetFeed()
    {
        _feedRequests.Cancel(); _feedRequests.Dispose(); _feedRequests = new();
        _feed.Clear(); _seen.Clear(); _sequence = 0; _feedLoading = false; _feedError = null;
        _feedHasMore = _feedOmitted = _evidenceUnavailable = false;
    }
    [JSInvokable] public void CloseDetails() { _selected = null; ResetFeed(); if (!_disposed) StateHasChanged(); }
    [JSInvokable] public void StopFollowing() { _follow = false; if (!_disposed) StateHasChanged(); }
    private void FollowChanged(ChangeEventArgs e) => _follow = e.Value is true;

    private async Task RefreshFeedAsync()
    {
        if (_selected is not { CanInspect: true } selected || (selected.AttemptId is null && selected.ModelRunId is null) || _feedLoading || _disposed) return;
        var token = _feedRequests.Token;
        _feedLoading = true; _feedError = null;
        try
        {
            // Bounded catch-up; normal updates arrive through audit outbox wake hints, not a polling loop.
            for (var pageNumber = 0; pageNumber < 4; pageNumber++)
            {
                var url = selected.ModelRunId.HasValue ?
                    $"api/core/organizations/{OrganizationId}/dashboard/activity/{selected.EmployeeId}/models/{selected.ModelRunId}/feed?afterSequence={_sequence}" :
                    $"api/core/organizations/{OrganizationId}/dashboard/activity/{selected.EmployeeId}/{selected.AttemptId}/feed?afterSequence={_sequence}" +
                    (selected.WorkItemId.HasValue ? $"&workItemId={selected.WorkItemId}" : "");
                var page = await Http.GetFromJsonAsync<CurrentActivityFeedPage>(url, token) ?? throw new HttpRequestException("Empty feed response.");
                if (token.IsCancellationRequested) return;
                foreach (var entry in page.Entries) if (_seen.Add((entry.Id, entry.Key))) _feed.Add(entry);
                _sequence = Math.Max(_sequence, page.NextSequence);
                _feedHasMore = page.HasMore; _feedOmitted |= page.EarlierEntriesOmitted; _evidenceUnavailable |= page.EvidenceUnavailable;
                if (_feed.Count > 1000)
                {
                    _feed.RemoveRange(0, _feed.Count - 1000); _seen.Clear(); foreach (var entry in _feed) _seen.Add((entry.Id, entry.Key)); _feedOmitted = true;
                }
                if (!page.HasMore) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            if (!token.IsCancellationRequested)
            {
                _feedError = "The feed is unavailable or access has changed. Refresh to try again.";
                if (error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound }) { _feed.Clear(); _seen.Clear(); _sequence = 0; }
            }
        }
        finally { if (!token.IsCancellationRequested) _feedLoading = false; }
    }

    private void OnEvent(AppRealtimeEventEnvelope e)
    {
        if (e.OrganizationId != OrganizationId || _disposed) return;
        if (e.EventType == AppRealtimeEvents.AuditChanged && (e.Data.ValueKind != JsonValueKind.Object ||
            !e.Data.TryGetProperty("employeeIds", out var employees) || employees.ValueKind != JsonValueKind.Array || employees.GetArrayLength() == 0)) return;
        if (e.EventType is AppRealtimeEvents.AuditChanged or AppRealtimeEvents.WorkBoardChanged or AppRealtimeEvents.EmployeeDirectoryChanged)
            QueueRefresh();
    }
    private void OnConnect() { _connected = true; QueueRefresh(); }
    private void OnDisconnect() { _connected = false; if (!_disposed) _ = InvokeAsync(StateHasChanged); }
    private void QueueRefresh() { if (!_disposed) _ = InvokeAsync(PumpAsync); }
    private async Task PumpAsync()
    {
        _refreshQueued = true;
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_refreshQueued && !_disposed)
            {
                _refreshQueued = false;
                await Task.Delay(750, _requests.Token);
                await RefreshAsync();
                if (!_disposed) StateHasChanged();
            }
        }
        catch (OperationCanceledException) { }
        finally { _pumping = false; }
    }
    private async Task UpdateClockAsync()
    {
        try { while (await _clock!.WaitForNextTickAsync()) { if (_disposed) return; await InvokeAsync(() => { _connected = Realtime.IsConnected; StateHasChanged(); }); } }
        catch (OperationCanceledException) { }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed) return;
        try
        {
            _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", "./_content/CSweet.UI/currentActivity.js");
            _reference ??= DotNetObjectReference.Create(this);
            if (_module is not null) await _module.InvokeVoidAsync("sync", _root, _reference, _selected?.Key, _follow);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or TaskCanceledException) { }
    }

    private static string DisplayState(CurrentActivityItem item) => item.State == "Executing" && item.LeaseExpiresAt.HasValue && item.LeaseExpiresAt <= DateTimeOffset.UtcNow ? "Recovering" :
        item.State == "Executing" && DateTimeOffset.UtcNow - (item.LastProgressAt ?? item.StartedAt) > TimeSpan.FromMinutes(5) ? "Unconfirmed" : item.State;
    private static string DisplayAction(CurrentActivityItem item) => DisplayState(item) != item.State ?
        DisplayState(item) == "Recovering" ? "Execution lease expired; awaiting recovery" : "No recent task progress reported" : item.CurrentAction;
    private static string StateClass(CurrentActivityItem item) => DisplayState(item) == "Executing" ? "executing" : "waiting";
    private static string StateIcon(CurrentActivityItem item) => DisplayState(item) == "Executing" ? Icons.Material.Outlined.PlayCircleOutline : Icons.Material.Outlined.Schedule;
    private static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
    private static string Age(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        return age.TotalSeconds < 60 ? $"{Math.Max(0, (int)age.TotalSeconds)}s" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m" : age.TotalHours < 24 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d";
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _clock?.Dispose(); _requests.Cancel(); _feedRequests.Cancel();
        Realtime.EventReceived -= OnEvent; Realtime.Connected -= OnConnect; Realtime.Reconnected -= OnConnect; Realtime.Disconnected -= OnDisconnect;
        if (_module is not null) { try { await _module.InvokeVoidAsync("dispose", _root); await _module.DisposeAsync(); } catch (Exception e) when (e is JSException or TaskCanceledException) { } }
        _reference?.Dispose(); _requests.Dispose(); _feedRequests.Dispose();
    }
}
