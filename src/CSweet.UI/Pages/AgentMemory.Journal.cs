using System.Net.Http.Json;
using CSweet.Contracts.Memory;

namespace CSweet.UI.Pages;

public partial class AgentMemory : IDisposable
{
    private string _view = "Journal";
    private bool _manageOpen, _filtersOpen, _loadingJournal, _erasureLocked;
    private string? _journalNotice, _pinnedError;
    private IReadOnlyList<AgentMemoryItemResponse> _pinned = [];
    private readonly List<JournalEntry> _events = [];
    private readonly HashSet<Guid> _activityLoaded = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool SelectionLocked => _reviewBusy || _pendingReview is not null || _procedureReviewLocked ||
        _sourceSuppressionLocked || _sourceHoldLocked || _erasureLocked;

    private sealed record JournalEntry(AgentMemoryItemResponse Item, string Label, string Text, DateTimeOffset At, string Key);
    private IEnumerable<JournalEntry> JournalEntries => (_page?.Items ?? [])
        .Select(x => new JournalEntry(x, "Recorded", x.Content, x.OccurredAt, $"record:{x.Id}"))
        .Concat(_events).DistinctBy(x => x.Key).OrderByDescending(x => x.At).ThenBy(x => x.Key);

    private async Task SearchAsync()
    {
        if (SelectionLocked || _loadingItems || _loadingJournal) return;
        await Task.WhenAll(LoadItemsAsync(), LoadGraphAsync());
    }

    private async Task ClearFiltersAsync()
    {
        if (SelectionLocked || _loadingItems || _loadingJournal) return;
        _search = _kind = _scope = _source = _sensitivity = _state = "";
        _from = _to = null;
        await SearchAsync();
    }

    private async Task ShowCoreAsync()
    {
        if (SelectionLocked || _loadingItems || _loadingJournal) return;
        _view = "All memory";
        _search = _scope = _source = _sensitivity = _state = "";
        _from = _to = null;
        _kind = "Core";
        _filtersOpen = true;
        await LoadItemsAsync();
    }

    private void CloseInspector()
    {
        if (SelectionLocked) return;
        _selected = null; _review = null; _reviewMessage = null;
    }

    private async Task RecordReviewedAsync(Guid id)
    {
        if (SelectionLocked) return;
        var previous = _selected?.Id;
        await InspectReviewSourceAsync(id);
        if (_selected?.Id == id) await RefreshSelectedRecordAsync(previous);
    }

    private async Task RefreshSelectedRecordAsync(Guid? previous)
    {
        if (_selected is not { } selected) return;
        if (_page is not null)
            _page = _page with { Items = _page.Items.Select(x => x.Id == previous ? selected : x).DistinctBy(x => x.Id).ToList() };
        _events.RemoveAll(x => x.Item.Id == previous || x.Item.Id == selected.Id);
        if (previous.HasValue) _activityLoaded.Remove(previous.Value);
        _activityLoaded.Remove(selected.Id);
        await Task.WhenAll(LoadPinnedAsync(), LoadJournalAsync());
    }

    private async Task InspectGraphAsync(Guid id)
    {
        if (SelectionLocked) return;
        try
        {
            var item = await Http.GetFromJsonAsync<AgentMemoryItemResponse>($"{BaseUrl}/items/{id}", _lifetime.Token);
            if (item is not null && !SelectionLocked) await SelectItemAsync(item);
        }
        catch (Exception) { _error = "This memory is unavailable or your access changed."; }
    }

    private async Task LoadPinnedAsync()
    {
        try
        {
            var page = await Http.GetFromJsonAsync<AgentMemoryPageResponse>($"{BaseUrl}/items?kind=Core&limit=100", _lifetime.Token);
            _pinned = (page?.Items ?? []).Where(x => x.State == "Pinned" ||
                x.Metadata?.GetValueOrDefault("pinned") == "true").ToList();
            _pinnedError = page?.NextCursor is not null ? "Pinned context is shown from the newest 100 core memories. Explore core memory for older records." : null;
        }
        catch (Exception) { _pinned = []; _pinnedError = "Pinned context could not be loaded. Explore core memory to try again."; }
    }

    // Bounded reads reuse the server's existing per-record history and recall authorization.
    // The journal explicitly describes its loaded-record window rather than claiming a global audit feed.
    private async Task LoadJournalAsync()
    {
        if (_loadingJournal || _lifetime.IsCancellationRequested) return;
        _loadingJournal = true; _journalNotice = null;
        try
        {
            foreach (var batch in (_page?.Items ?? []).Where(x => !_activityLoaded.Contains(x.Id)).Chunk(4))
            {
                var results = await Task.WhenAll(batch.Select(ReadActivityAsync));
                foreach (var result in results)
                {
                    if (result.Complete) _activityLoaded.Add(result.Id);
                    else _journalNotice = "Some review or recall activity is unavailable. Recorded memories are still shown.";
                    _events.RemoveAll(x => x.Item.Id == result.Id);
                    _events.AddRange(result.Entries);
                }
                if (_loadedIdentity.HasValue && !_lifetime.IsCancellationRequested) StateHasChanged();
            }
        }
        finally { _loadingJournal = false; }
    }

    private async Task<(Guid Id, bool Complete, List<JournalEntry> Entries)> ReadActivityAsync(AgentMemoryItemResponse item)
    {
        var entries = new List<JournalEntry>();
        try
        {
            var detailTask = Http.GetFromJsonAsync<AgentMemoryItemResponse>($"{BaseUrl}/items/{item.Id}", _lifetime.Token);
            var historyTask = Http.GetFromJsonAsync<MemoryHistoryPage>($"{BaseUrl}/history/{Uri.EscapeDataString(item.Kind)}/{item.Id}?limit=20", _lifetime.Token);
            await Task.WhenAll(detailTask, historyTask);
            foreach (var use in (await detailTask)?.RecallUses ?? [])
                entries.Add(new(item, "Recalled", "Used in a conversation. Open this memory to inspect the recall record.", use.UsedAt,
                    $"recall:{item.Id}:{use.ConversationId}:{use.UsedAt:O}:{use.Layer}"));
            foreach (var snapshot in (await historyTask)?.Items ?? [])
            {
                entries.Add(new(item, $"Revision {snapshot.Revision} · {snapshot.Operation}", snapshot.Content, snapshot.RecordedAt,
                    $"revision:{item.Id}:{snapshot.Revision}"));
                foreach (var review in snapshot.Reviews)
                    entries.Add(new(item, review.Action, $"Reviewed by {review.ReviewerName}", review.ReviewedAt, $"review:{review.ReceiptId}"));
            }
            return (item.Id, true, entries);
        }
        catch (Exception) { return (item.Id, false, entries); }
    }

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
