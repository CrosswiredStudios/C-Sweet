using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Contracts.Realtime;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CSweet.UI.Components.Communications;

public partial class ShareWorkInstruction
{
    [Inject] public HttpClient Http { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public AppRealtimeState Realtime { get; set; } = default!;
    [Parameter, EditorRequired] public Guid OrganizationId { get; set; }
    [Parameter, EditorRequired] public Guid ConversationId { get; set; }
    [Parameter, EditorRequired] public Guid MessageId { get; set; }
    [Parameter, EditorRequired] public string Content { get; set; } = "";

    private bool _open, _busy, _uncertain;
    private bool _refreshPending, _disposed;
    private int _generation;
    private string? _error;
    private Guid? _board, _item, _next;
    private Guid _operation;
    private WorkInstructionPreview? _preview;
    private SelectWorkInstructionRequest? _selection;
    private WorkInstructionPublicationResponse? _result;
    private IReadOnlyList<WorkBoardSummaryResponse> _boards = [];
    private IReadOnlyList<WorkBoardItemResponse> _items = [];
    private readonly List<WorkInstructionPublicationResponse> _history = [];
    private ElementReference _source;
    private IJSObjectReference? _module;
    private (Guid, Guid, Guid, string)? _identity;
    private string HeadingId => $"instruction-heading-{MessageId:N}";
    private string SourceId => $"instruction-source-{MessageId:N}";
    private string BoardId => $"instruction-board-{MessageId:N}";
    private string ItemId => $"instruction-item-{MessageId:N}";
    private string DirectoryPath => $"api/organizations/{OrganizationId:D}/work/boards";
    private string InstructionPath => $"{DirectoryPath}/{_board:D}/items/{_item:D}/instructions";
    private string WorkHref => $"/organizations/{OrganizationId:D}/work/boards/{_board:D}?item={_item:D}";

    protected override void OnInitialized()
    {
        Realtime.EventReceived += OnEvent;
        Realtime.Connected += OnReconnect;
        Realtime.Reconnected += OnReconnect;
    }

    private void OnEvent(AppRealtimeEventEnvelope hint)
    {
        if (!_open || hint.OrganizationId != OrganizationId || hint.EventType != AppRealtimeEvents.WorkBoardChanged ||
            hint.Data.ValueKind != JsonValueKind.Object || !hint.Data.TryGetProperty("boardId", out var board) || board.ValueKind != JsonValueKind.String ||
            !board.TryGetGuid(out var boardId) || boardId != _board) return;
        if (hint.Data.TryGetProperty("itemId", out var item) &&
            (item.ValueKind != JsonValueKind.String || !item.TryGetGuid(out var itemId) || itemId != _item)) return;
        OnReconnect();
    }

    private void OnReconnect()
    {
        if (!_open || _item is null || _disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed || !_open || _item is null) return;
            _refreshPending = true;
            if (!_busy) { _refreshPending = false; await RefreshPublicationsAsync(); }
        });
    }

    protected override void OnParametersSet()
    {
        var identity = (OrganizationId, ConversationId, MessageId, Content);
        if (_identity == identity) return;
        _identity = identity;
        Close();
    }

    private void Close()
    {
        ++_generation; _open = _busy = _uncertain = _refreshPending = false; _error = null;
        _board = _item = _next = null; _preview = null; _selection = null; _result = null;
        _boards = []; _items = []; _history.Clear(); _operation = Guid.Empty;
    }

    private async Task OpenAsync()
    {
        Close(); _open = true;
        await RunAsync(async () =>
        {
            var generation = _generation;
            var directory = await Http.GetFromJsonAsync<WorkBoardDirectoryResponse>(DirectoryPath);
            if (generation == _generation) _boards = directory?.Boards.Where(x => !x.IsArchived).ToArray() ?? [];
        });
    }

    private void ClearReview() { _preview = null; _selection = null; _operation = Guid.Empty; _error = null; }

    private async Task ChangeBoardAsync(ChangeEventArgs args)
    {
        ++_generation; _busy = _refreshPending = false; ClearReview(); _result = null; _uncertain = false;
        _board = Guid.TryParse(args.Value?.ToString(), out var id) ? id : null;
        _item = _next = null; _items = []; _history.Clear();
        if (_board is null) return;
        await RunAsync(async () =>
        {
            var generation = _generation;
            var detail = await Http.GetFromJsonAsync<WorkBoardDetailResponse>($"{DirectoryPath}/{_board:D}");
            if (generation == _generation) _items = detail?.Items ?? [];
        });
    }

    private async Task ChangeItemAsync(ChangeEventArgs args)
    {
        ++_generation; _busy = _refreshPending = false; ClearReview(); _result = null; _uncertain = false;
        _item = Guid.TryParse(args.Value?.ToString(), out var id) ? id : null;
        _next = null; _history.Clear();
        if (_item is not null) await RefreshPublicationsAsync();
    }

    private async Task PreviewAsync() => await RunAsync(async () =>
    {
        var generation = _generation;
        _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/CSweet.UI/js/workInstruction.js");
        if (generation != _generation) return;
        var range = await _module.InvokeAsync<int[]>("selection", _source, Content);
        if (generation != _generation) return;
        if (range.Length != 2 || range[0] < 0 || range[1] <= 0 || range[1] > 8192)
            throw new InvalidOperationException("Highlight the relevant instruction text first (up to 8,192 characters).");
        var selection = new SelectWorkInstructionRequest(ConversationId, MessageId, range[0], range[1]);
        using var response = await Http.PostAsJsonAsync(InstructionPath + "/preview", selection);
        if (generation != _generation) return;
        await RequireSuccessAsync(response);
        var preview = await response.Content.ReadFromJsonAsync<WorkInstructionPreview>() ?? throw new JsonException();
        if (generation != _generation) return;
        _selection = selection; _preview = preview; _operation = Guid.NewGuid();
    });

    private async Task PublishAsync()
    {
        if (_preview is null || _selection is null || _operation == Guid.Empty) return;
        await RunAsync(async () =>
        {
            var generation = _generation;
            // Freeze the reviewed request across response loss. No new operation is
            // allocated until the user reviews a new publication.
            var request = new PublishWorkInstructionRequest(_operation, _selection, _preview.ReviewToken);
            _uncertain = true;
            using var response = await Http.PostAsJsonAsync(InstructionPath + "/publish", request);
            if (generation != _generation) return;
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                _uncertain = false; ClearReview();
            }
            await RequireSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<WorkInstructionPublicationResponse>() ?? throw new JsonException();
            if (generation != _generation) return;
            _result = result; _uncertain = false;
        });
    }

    private Task RefreshPublicationsAsync() => LoadPublicationsAsync(append: false);
    private Task LoadMoreAsync() => LoadPublicationsAsync(append: true);
    private Task LoadPublicationsAsync(bool append) => RunAsync(async () =>
    {
        var generation = _generation;
        var path = InstructionPath + "/publications" + (append && _next is not null ? $"?cursor={_next:D}" : "");
        using var response = await Http.GetAsync(path);
        if (generation != _generation) return;
        // Never leave cached receipt metadata visible after authority is denied.
        if (!response.IsSuccessStatusCode) { _history.Clear(); _next = null; }
        if (response.StatusCode == HttpStatusCode.Forbidden) { ClearReview(); _result = null; _uncertain = false; }
        await RequireSuccessAsync(response);
        var page = await response.Content.ReadFromJsonAsync<WorkInstructionPublicationPage>() ?? throw new JsonException();
        if (generation != _generation) return;
        if (!append) _history.Clear();
        foreach (var publication in page.Items)
            if (_history.All(x => x.Id != publication.Id)) _history.Add(publication);
        _next = page.NextCursor;
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || !_open) return;
        var generation = _generation; _busy = true; _error = null;
        try { await action(); }
        catch (Exception error) when (error is HttpRequestException or JsonException or JSException or InvalidOperationException or TaskCanceledException)
        { if (generation == _generation) _error = error is InvalidOperationException ? error.Message : "The request could not be completed. Try again."; }
        finally
        {
            if (generation == _generation)
            {
                _busy = false; StateHasChanged();
                if (_refreshPending && _item is not null && _open && !_disposed)
                { _refreshPending = false; await RefreshPublicationsAsync(); }
            }
        }
    }

    private static Task RequireSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You no longer have access to this instruction or work item.",
            HttpStatusCode.Conflict => "The instruction or audience changed. Review the current selection again.",
            HttpStatusCode.BadRequest => "This selection cannot be published. Choose relevant text from your own message.",
            _ => "The request could not be completed. Try again."
        });
    }

    private static string StatusText(string status) => status switch
    { "Published" => "published", "Changed" => "edited since publication", "Withdrawn" => "withdrawn", _ => "unavailable" };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Realtime.EventReceived -= OnEvent; Realtime.Connected -= OnReconnect; Realtime.Reconnected -= OnReconnect;
        Close();
        if (_module is not null)
            try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
