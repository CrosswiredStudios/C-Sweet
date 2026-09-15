using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.WorkBoards;

/// <summary>
/// Durable ticket discussion. Loads the collaboration payload for one work item, so the server
/// decides who may post and which comments the viewer may change, then renders author, timestamp,
/// and an edit/delete affordance only on the viewer's own comments.
/// </summary>
public partial class WorkItemComments
{
    private const string AgentAuthorKind = "AgentInstallation";

    [Inject] public HttpClient Http { get; set; } = default!;

    [Parameter, EditorRequired] public Guid OrganizationId { get; set; }
    [Parameter, EditorRequired] public Guid BoardId { get; set; }
    [Parameter, EditorRequired] public Guid ItemId { get; set; }
    /// <summary>Renders tighter spacing for the ticket drawer rather than the dialog.</summary>
    [Parameter] public bool Compact { get; set; }

    private readonly List<WorkItemCommentResponse> _comments = [];
    private Guid? _loadedItemId;
    private Guid? _currentOrganizationUserId;
    private string _draft = string.Empty;
    private string _editDraft = string.Empty;
    private Guid? _editingId;
    private Guid? _deleteConfirmId;
    private string _notice = string.Empty;
    private string? _error;
    private bool _loading;
    private bool _busy;
    private bool _saving;
    private bool _canComment;

    private string ComposerFieldId => $"comment-composer-{ItemId:N}";

    private string ItemPath =>
        $"api/organizations/{OrganizationId:D}/work/boards/{BoardId:D}/items/{ItemId:D}";

    private string CollaborationPath => $"{ItemPath}/collaboration";

    private string CommentsPath => $"{ItemPath}/comments";

    private static string EditFieldId(Guid commentId) => $"comment-edit-{commentId:N}";

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedItemId == ItemId) return;
        _loadedItemId = ItemId;
        _draft = string.Empty;
        _editDraft = string.Empty;
        _editingId = null;
        _deleteConfirmId = null;
        _notice = string.Empty;
        await ReloadAsync();
    }

    /// <summary>Reloads the thread. Parents call this after a realtime board event.</summary>
    public async Task ReloadAsync()
    {
        if (ItemId == Guid.Empty || BoardId == Guid.Empty) return;
        _loading = true;
        _error = null;
        try
        {
            var response = await Http.GetAsync(CollaborationPath);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _comments.Clear();
                _canComment = false;
                _error = "You do not have access to this ticket's comments.";
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                _error = await ReadFailureAsync(response);
                return;
            }
            var payload = await response.Content.ReadFromJsonAsync<WorkItemCollaborationResponse>();
            _comments.Clear();
            if (payload is null) return;
            _comments.AddRange(payload.Comments.OrderByDescending(x => x.CreatedAt));
            _canComment = payload.CanComment;
            _currentOrganizationUserId = payload.CurrentOrganizationUserId;
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
        catch (JsonException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void BeginEdit(WorkItemCommentResponse comment)
    {
        _deleteConfirmId = null;
        _editingId = comment.Id;
        _editDraft = comment.Body;
        _error = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editDraft = string.Empty;
    }

    private async Task AddAsync()
    {
        if (_saving || string.IsNullOrWhiteSpace(_draft)) return;
        _saving = true;
        _notice = string.Empty;
        _error = null;
        try
        {
            var response = await Http.PostAsJsonAsync(
                CommentsPath,
                new AddWorkItemCommentRequest(_draft.Trim(), Guid.NewGuid().ToString("N")));
            if (!await EnsureAcceptedAsync(response)) return;
            _draft = string.Empty;
            _notice = "Comment posted";
            await ReloadAsync();
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task SaveEditAsync()
    {
        if (_busy || _editingId is not { } commentId || string.IsNullOrWhiteSpace(_editDraft)) return;
        var comment = _comments.FirstOrDefault(x => x.Id == commentId);
        if (comment is null) return;
        _busy = true;
        _notice = string.Empty;
        _error = null;
        try
        {
            var response = await Http.PutAsJsonAsync(
                $"{CommentsPath}/{commentId:D}",
                new UpdateWorkItemCommentRequest(
                    _editDraft.Trim(), comment.Revision, Guid.NewGuid().ToString("N")));
            if (!await EnsureAcceptedAsync(response)) return;
            _editingId = null;
            _editDraft = string.Empty;
            _notice = "Comment updated";
            await ReloadAsync();
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ConfirmDeleteAsync(WorkItemCommentResponse comment)
    {
        if (_busy) return;
        _busy = true;
        _notice = string.Empty;
        _error = null;
        try
        {
            var response = await Http.PostAsJsonAsync(
                $"{CommentsPath}/{comment.Id:D}/delete",
                new DeleteWorkItemCommentRequest(comment.Revision, Guid.NewGuid().ToString("N")));
            if (!await EnsureAcceptedAsync(response)) return;
            _deleteConfirmId = null;
            _notice = "Comment deleted";
            await ReloadAsync();
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<bool> EnsureAcceptedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return true;
        _error = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Only the comment author can change this comment.",
            HttpStatusCode.Conflict =>
                "This comment changed since the ticket was loaded. Refresh and try again.",
            HttpStatusCode.NotFound => "The comment no longer exists on this ticket.",
            _ => await ReadFailureAsync(response)
        };
        return false;
    }

    private static async Task<string> ReadFailureAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? body;
                }
                return body;
            }
        }
        catch (JsonException)
        {
            // Fall through to the status-code message when the body is not JSON.
        }
        return $"The comment request failed ({(int)response.StatusCode}).";
    }

    private bool IsMine(WorkItemCommentResponse comment) =>
        _currentOrganizationUserId.HasValue &&
        comment.AuthorKind != AgentAuthorKind &&
        comment.AuthorSubjectId == _currentOrganizationUserId.Value;

    private string Initials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }

    private static string RelativeTime(DateTimeOffset value)
    {
        var age = DateTimeOffset.UtcNow - value;
        return age.TotalMinutes < 1 ? "Just now" :
            age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago" :
            age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago" :
            $"{(int)age.TotalDays}d ago";
    }
}
