using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Present = CSweet.UI.Components.Employees.PersonalBoardPresentation;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.Employees;

public partial class EmployeePersonalBoard
{
    [Inject] public HttpClient Http { get; set; } = default!;
    [Parameter, EditorRequired] public Guid OrganizationId { get; set; }
    [Parameter, EditorRequired] public Guid EmployeeId { get; set; }
    [Parameter, EditorRequired] public Wire.PersonalTodoBoard Board { get; set; } = default!;
    [Parameter] public bool CanAdd { get; set; }
    [Parameter] public bool CanExecute { get; set; }
    [Parameter] public bool IncludeArchived { get; set; }
    [Parameter] public EventCallback<bool> IncludeArchivedChanged { get; set; }
    [Parameter] public EventCallback RefreshRequested { get; set; }
    [Parameter] public EventCallback CloseRequested { get; set; }

    private static readonly string[] Priorities = ["Low", "Medium", "High", "Critical"];
    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private DialogOptions TaskDialogOptions => new()
    {
        NoHeader = true, FullWidth = true, MaxWidth = MaxWidth.Medium,
        BackdropClick = !_busy, CloseOnEscapeKey = !_busy
    };
    private MudMenu? _filterMenu;
    private HashSet<string> _statuses = [];
    private HashSet<string> _draftStatuses = [];
    private string _priority = "", _archive = "current", _draftPriority = "", _draftArchive = "current";
    private string? _error;
    private string _notice = "";
    private bool _busy, _creating, _dialogOpen, _editingDetails, _transferring;
    private Wire.PersonalTodoItem? _selectedItem, _draggedItem;
    private string? _dropTarget;
    private string _editTitle = "", _editDescription = "", _editPriority = "Medium", _editStatus = Wire.PersonalTodoStatuses.Ready;
    private string? _blockReason;
    private IReadOnlyList<Wire.WorkItemMentionInput> _editTitleMentions = [], _editDescriptionMentions = [];
    private IReadOnlyList<OrganizationUserResponse> _people = [];
    private Guid? _loadedOrganizationId, _loadedBoardId, _transferBoardId;
    private IReadOnlyList<WorkBoardSummaryResponse> _transferTargets = [];

    private IReadOnlyList<Wire.PersonalTodoItem> FilteredItems => Board.Items
        .Where(x => Present.Matches(x, _statuses, _priority, _archive)).OrderBy(x => x.Rank).ToList();
    private IReadOnlyList<Wire.PersonalTodoItem> ItemsFor(string status) => FilteredItems.Where(x => x.Status == status).ToList();
    private int FilterCount => (_statuses.Count > 0 ? 1 : 0) + (_priority.Length > 0 ? 1 : 0) + (_archive != "current" ? 1 : 0);
    private bool HasEditableFields => _creating || (_selectedItem is { ArchivedAt: null } item && (CanExecute || CanMoveAny(item)));
    private bool HasDetailsChanges => _selectedItem is { } item &&
        (_editTitle != item.Title || _editDescription != item.Description || _editPriority != Present.Priority(item.Priority));
    private bool HasStatusChanges => _selectedItem is { } item &&
        (_editStatus != item.Status || (CanExecute && _editStatus == Wire.PersonalTodoStatuses.Blocked && _blockReason != item.BlockReason));
    private bool CanSave => HasEditableFields && !string.IsNullOrWhiteSpace(_editTitle) &&
        (_editStatus != Wire.PersonalTodoStatuses.Blocked || !CanExecute || !string.IsNullOrWhiteSpace(_blockReason)) &&
        (_creating || HasDetailsChanges || HasStatusChanges);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedBoardId != Board.BoardId)
        {
            _loadedBoardId = Board.BoardId;
            _statuses.Clear(); _priority = ""; _archive = IncludeArchived ? "all" : "current";
            _dialogOpen = false; _selectedItem = null; _error = null; _notice = "";
        }
        // Do not overwrite a user's draft when a realtime board refresh arrives. The save
        // keeps the original revision so a concurrent edit is detected by the server.
        if (_loadedOrganizationId == OrganizationId || !(CanAdd || CanExecute)) return;
        try
        {
            _people = (await Http.GetFromJsonAsync<IReadOnlyList<OrganizationUserResponse>>(
                $"api/core/organizations/{OrganizationId:D}/users") ?? [])
                .Where(x => x.IsActive).OrderBy(x => x.DisplayName).ToList();
            _loadedOrganizationId = OrganizationId;
        }
        catch (HttpRequestException) { _people = []; }
        catch (JsonException) { _people = []; }
    }

    private void PrepareFilters()
    { _draftStatuses = [.. _statuses]; _draftPriority = _priority; _draftArchive = _archive; }
    private void ResetDraftFilters() { _draftStatuses.Clear(); _draftPriority = ""; _draftArchive = "current"; }
    private void ToggleStatusFilter(string status) { if (!_draftStatuses.Add(status)) _draftStatuses.Remove(status); }
    private async Task CloseFiltersAsync() { if (_filterMenu is not null) await _filterMenu.CloseMenuAsync(); }
    private async Task ApplyFiltersAsync()
    {
        if (_busy) return;
        await CloseFiltersAsync();
        _busy = true; _error = null;
        try
        {
            var include = _draftArchive != "current";
            if (IncludeArchivedChanged.HasDelegate && include != IncludeArchived)
                await IncludeArchivedChanged.InvokeAsync(include);
            _statuses = [.. _draftStatuses]; _priority = _draftPriority; _archive = _draftArchive;
            _notice = $"{FilteredItems.Count} matching tickets";
        }
        catch (Exception exception) { _error = exception.Message; }
        finally { _busy = false; }
    }
    private async Task ClearFiltersAsync() { ResetDraftFilters(); await ApplyFiltersAsync(); }

    private static string Short(Guid? value) => value?.ToString("N")[..8].ToUpperInvariant() ?? "";
    private string BoardHref(Wire.PersonalTodoItem item) => $"/organizations/{OrganizationId:D}/work/boards/{item.WorkContext!.BoardId!.Value:D}";
    private static string TechnicalDetails(Wire.PersonalTodoItem item) => JsonSerializer.Serialize(item, DetailJson);
    private bool CanMoveAny(Wire.PersonalTodoItem item) =>
        (item.PlanRootId is null || item.PlanRootId == item.Id) &&
        Present.Columns.Any(x => Present.CanMove(item, x.Status, CanExecute, CanAdd));
    private bool CanDrag(Wire.PersonalTodoItem item) => !_busy && CanMoveAny(item);
    private void StartDrag(Wire.PersonalTodoItem item) { if (CanDrag(item)) _draggedItem = item; }
    private void EndDrag() { _draggedItem = null; _dropTarget = null; }
    private void SetDropTarget(string status) => _dropTarget = _draggedItem is { } item &&
        Present.CanMove(item, status, CanExecute, CanAdd) ? status : null;
    private async Task DropAsync(string status)
    {
        var item = _draggedItem; EndDrag();
        if (_busy || item is null || !Present.CanMove(item, status, CanExecute, CanAdd)) return;
        if (status == Wire.PersonalTodoStatuses.Blocked)
        {
            OpenDetails(item); _editStatus = status;
            return;
        }
        await MutateAsync(async () =>
        {
            await MoveItemAsync(item, status);
            _notice = $"{item.Title} moved to {Present.StatusLabel(status)}";
        });
    }

    private void OpenCreate()
    {
        if (_busy || !CanAdd) return;
        _selectedItem = null; _creating = true; _editingDetails = false; _transferring = false;
        _editTitle = ""; _editDescription = ""; _editPriority = "Medium"; _editStatus = Wire.PersonalTodoStatuses.Ready;
        _editTitleMentions = []; _editDescriptionMentions = []; _blockReason = null;
        _error = null; _dialogOpen = true;
    }
    private void OpenDetails(Wire.PersonalTodoItem item)
    {
        if (_busy) return;
        _selectedItem = item; _creating = false; _editingDetails = false; _transferring = false; _transferBoardId = null;
        _editTitle = item.Title; _editDescription = item.Description; _editPriority = Present.Priority(item.Priority);
        _editStatus = item.Status; _blockReason = item.BlockReason;
        _editTitleMentions = Mentions(item, Wire.WorkItemMentionFields.Title);
        _editDescriptionMentions = Mentions(item, Wire.WorkItemMentionFields.Description);
        _error = null; _dialogOpen = true;
    }
    private static IReadOnlyList<Wire.WorkItemMentionInput> Mentions(Wire.PersonalTodoItem item, string field) =>
        item.MentionSpans.Where(x => x.Field == field)
            .Select(x => new Wire.WorkItemMentionInput(x.OrganizationUserId, x.Field, x.Offset, x.Length)).ToList();
    private void CloseDetails() { if (!_busy) { _dialogOpen = false; _error = null; } }

    private async Task SaveAsync()
    {
        if (_busy || !CanSave) return;
        var success = await MutateAsync(async () =>
        {
            if (_creating)
            {
                if (!CanAdd) throw new InvalidOperationException("You cannot create work on this board.");
                _selectedItem = await ReadItemAsync(await Http.PostAsJsonAsync(
                    $"api/organizations/{OrganizationId}/work/personal-todos/items",
                    new Wire.AddPersonalTodoItemRequest(_editTitle, _editDescription, _editPriority, null,
                        $"personal-board:add:{Guid.NewGuid():N}", EmployeeId,
                        Mentions: _editTitleMentions.Concat(_editDescriptionMentions).ToList())));
                _creating = false;
            }
            else if (_selectedItem is { } item)
            {
                if (HasDetailsChanges)
                {
                    if (!CanExecute || item.ArchivedAt.HasValue) throw new InvalidOperationException("This ticket is read only.");
                    // Use the returned revision for the subsequent status request. If the
                    // latter fails, preserve the successful edit and its new revision.
                    item = await ReadItemAsync(await Http.PutAsJsonAsync(
                        $"api/organizations/{OrganizationId}/work/personal-todos/items",
                        new Wire.UpdatePersonalTodoItemRequest(item.Id, _editTitle, _editDescription, _editPriority,
                            item.DueDate, item.Revision, $"personal-board:update:{Guid.NewGuid():N}",
                            _editTitleMentions.Concat(_editDescriptionMentions).ToList())));
                    _selectedItem = item;
                }
                if (HasStatusChanges) _selectedItem = await MoveItemAsync(item, _editStatus, _blockReason);
            }
            _notice = "Ticket saved";
        });
        if (success) _dialogOpen = false;
    }

    private async Task<Wire.PersonalTodoItem> MoveItemAsync(Wire.PersonalTodoItem item, string status, string? reason = null)
    {
        if (item.PlanRootId is { } rootId && rootId != item.Id)
            throw new InvalidOperationException("Progress is managed through the parent epic. Requeue the epic to resume its plan.");
        var editingBlockReason = CanExecute && item.ArchivedAt is null && item.Status == status && status == Wire.PersonalTodoStatuses.Blocked;
        if (!editingBlockReason && !Present.CanMove(item, status, CanExecute, CanAdd))
            throw new InvalidOperationException("This move is not available for this ticket.");
        var path = $"api/organizations/{OrganizationId}/work/personal-todos/items";
        if (!CanExecute)
        {
            return item.Status == Wire.PersonalTodoStatuses.Backlog
                ? await ReadItemAsync(await Http.PostAsJsonAsync($"{path}/activate",
                    new Wire.ActivatePersonalTodoItemRequest(item.Id, item.Revision, $"personal-board:activate:{Guid.NewGuid():N}")))
                : await ReadItemAsync(await Http.PostAsJsonAsync($"{path}/requeue",
                    new Wire.RequeuePersonalTodoItemRequest(item.Id, item.Revision, $"personal-board:requeue:{Guid.NewGuid():N}")));
        }
        return await ReadItemAsync(await Http.PostAsJsonAsync($"{path}/status",
            new Wire.SetHumanPersonalTodoStatusRequest(item.Id, status, item.Revision, null, reason,
                $"personal-board:status:{Guid.NewGuid():N}")));
    }

    private IReadOnlyList<Wire.PersonalTodoItem> ReadyItems => Board.Items.Where(x => x.ArchivedAt is null &&
        x.Status == Wire.PersonalTodoStatuses.Ready).OrderBy(x => x.Rank).ToList();
    private bool CanReorder(Wire.PersonalTodoItem item, int direction)
    {
        var ready = ReadyItems.ToList(); var index = ready.FindIndex(x => x.Id == item.Id);
        return CanAdd && index >= 0 && index + direction >= 0 && index + direction < ready.Count;
    }
    private async Task ReorderAsync(Wire.PersonalTodoItem item, int direction)
    {
        if (_busy || !CanReorder(item, direction)) return;
        var ready = ReadyItems.ToList(); var index = ready.FindIndex(x => x.Id == item.Id); var target = index + direction;
        ready.RemoveAt(index);
        var before = target >= ready.Count ? (Guid?)null : ready[target].Id;
        await MutateAsync(async () =>
        {
            _selectedItem = await ReadItemAsync(await Http.PostAsJsonAsync(
                $"api/organizations/{OrganizationId}/work/personal-todos/items/reorder",
                new Wire.ReorderPersonalTodoItemRequest(item.Id, before, item.Revision, $"personal-board:reorder:{Guid.NewGuid():N}")));
            _notice = "Queue order updated";
        });
    }
    private async Task ArchiveAsync()
    {
        if (_busy || !CanExecute || _selectedItem is not { ArchivedAt: null } item) return;
        if (await MutateAsync(async () =>
        {
            _selectedItem = await ReadItemAsync(await Http.PostAsJsonAsync(
                $"api/organizations/{OrganizationId}/work/personal-todos/items/archive",
                new Wire.ArchivePersonalTodoItemRequest(item.Id, item.Revision, $"personal-board:archive:{Guid.NewGuid():N}")));
            _notice = "Ticket archived";
        })) _dialogOpen = false;
    }
    private async Task RestoreAsync()
    {
        if (_busy || !CanExecute || _selectedItem is not { ArchivedAt: not null } item) return;
        if (await MutateAsync(async () =>
        {
            _selectedItem = await ReadItemAsync(await Http.PostAsJsonAsync(
                $"api/organizations/{OrganizationId}/work/personal-todos/items/restore",
                new Wire.RestorePersonalTodoItemRequest(item.Id, item.Revision, $"personal-board:restore:{Guid.NewGuid():N}")));
            _notice = "Ticket restored";
        })) _dialogOpen = false;
    }
    private async Task BeginTransferAsync()
    {
        if (_busy || !CanExecute) return;
        if (_transferring) { _transferring = false; return; }
        _busy = true; _error = null;
        try
        {
            var directory = await Http.GetFromJsonAsync<WorkBoardDirectoryResponse>($"api/organizations/{OrganizationId}/work/boards");
            _transferTargets = directory?.Boards.Where(x => !x.IsArchived && x.Id != Board.BoardId &&
                x.AllowedActions.Contains(WorkItemActions.Create) && x.AllowedActions.Contains(WorkBoardActions.Read))
                .OrderBy(x => x.Name).ToList() ?? [];
            _transferring = true;
        }
        catch (Exception exception) { _error = exception.Message; }
        finally { _busy = false; }
    }
    private async Task TransferAsync()
    {
        if (_busy || !CanExecute || _selectedItem is not { } item || !_transferBoardId.HasValue) return;
        if (await MutateAsync(async () =>
        {
            using var response = await Http.PostAsJsonAsync(
                $"api/organizations/{OrganizationId}/work/boards/{item.BoardId}/items/{item.Id}/transfer",
                new TransferWorkItemRequest(_transferBoardId.Value, null, item.Revision, $"personal-board:transfer:{Guid.NewGuid():N}"));
            await EnsureSuccessAsync(response);
            _notice = "Ticket transferred";
        })) { _transferring = false; _dialogOpen = false; }
    }

    private async Task<bool> MutateAsync(Func<Task> action)
    {
        if (_busy) return false;
        _busy = true; _error = null;
        var success = false;
        try { await action(); success = true; }
        catch (Exception exception) { _error = exception.Message; }
        // Also refresh after conflicts or partially successful multi-step updates.
        try { await RefreshRequested.InvokeAsync(); }
        catch (Exception exception) { _error = $"{_error} Could not refresh the board: {exception.Message}".Trim(); success = false; }
        finally { _busy = false; }
        return success;
    }
    private static async Task<Wire.PersonalTodoItem> ReadItemAsync(HttpResponseMessage response)
    {
        using (response)
        {
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<Wire.PersonalTodoItem>()
                ?? throw new InvalidOperationException("The server returned no ticket details. Refresh the board before retrying.");
        }
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("This ticket changed since you opened it. Close and reopen it to review the latest changes before trying again.");
        var message = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("message", out var value)) message = value.GetString() ?? message;
        }
        catch (JsonException) { }
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"The task update failed ({(int)response.StatusCode})." : message);
    }
}
