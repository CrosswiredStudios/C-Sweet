using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Memory;

namespace CSweet.UI.Pages;

public partial class AgentMemory
{
    private MemoryClaimReviewResponse? _review;
    private ReviewMemoryClaimRequest? _pendingReview;
    private string? _reviewMessage;
    private string? _replacementValue;
    private bool _reviewBusy;
    private bool _procedureReviewLocked;
    private bool _sourceSuppressionLocked;
    private bool _sourceHoldLocked;
    private string _entitySearch = "";
    private IReadOnlyList<MemoryClaimCorrectionTarget> _correctionTargets = [];
    private MemoryClaimCorrectionTarget? _selectedCorrectionTarget;

    private async Task FindCorrectionTargetsAsync()
    {
        if (_reviewBusy || _pendingReview is not null || _review is not { IsEntityValued: true } review) return;
        _reviewBusy = true; _correctionTargets = []; _selectedCorrectionTarget = null; _reviewMessage = null;
        try
        {
            using var response = await Http.GetAsync($"{BaseUrl}/claims/{review.ClaimId}/review/entities?search={Uri.EscapeDataString(_entitySearch)}");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            { _review = null; _reviewMessage = "This claim is unavailable or your review permission changed."; return; }
            response.EnsureSuccessStatusCode();
            _correctionTargets = await response.Content.ReadFromJsonAsync<MemoryClaimCorrectionTarget[]>() ?? [];
            if (_correctionTargets.Count == 0) _reviewMessage = "No eligible entities matched. Try a more specific name or check their source evidence.";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { _reviewMessage = "Entity choices could not be loaded. Check the search and try again."; }
        finally { _reviewBusy = false; }
    }

    private async Task InspectReviewSourceAsync(Guid sourceId)
    {
        if (_reviewBusy || _pendingReview is not null || _procedureReviewLocked || _sourceSuppressionLocked || _sourceHoldLocked) return;
        _reviewBusy = true;
        try
        {
            var item = await Http.GetFromJsonAsync<AgentMemoryItemResponse>($"{BaseUrl}/items/{sourceId}");
            if (item is null) { _reviewMessage = "This source is no longer available."; return; }
            _selected = item; _review = null; _reviewMessage = null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { _reviewMessage = "This source could not be loaded. Refresh the claim before reviewing it."; }
        finally { _reviewBusy = false; }
    }

    private async Task LoadClaimReviewAsync()
    {
        if (_reviewBusy || _pendingReview is not null || _selected is not { Kind: "Claim" } selected) return;
        _correctionTargets = []; _selectedCorrectionTarget = null; _entitySearch = "";
        _reviewBusy = true; _reviewMessage = null; _pendingReview = null; _review = null;
        try
        {
            using var response = await Http.GetAsync($"{BaseUrl}/claims/{selected.Id}/review");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                _reviewMessage = "Review requires an authorized human manager. Relationship claims can only be reviewed by that relationship's participant.";
                return;
            }
            if (!response.IsSuccessStatusCode) { _reviewMessage = "This claim cannot currently be reviewed. Refresh its details or try again later."; return; }
            var review = await response.Content.ReadFromJsonAsync<MemoryClaimReviewResponse>();
            if (_selected?.Id == selected.Id) _review = review;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { _reviewMessage = "Review details could not be loaded. Try again."; }
        finally { _reviewBusy = false; }
    }

    private async Task SubmitClaimReviewAsync(string action)
    {
        if (_reviewBusy || _review is not { } review) return;
        if (action == "correct" && review.IsEntityValued && _selectedCorrectionTarget is null && _pendingReview is null) return;
        _pendingReview ??= new(Guid.NewGuid(), review.Revision, review.EvidenceToken, action,
            action == "correct" && !review.IsEntityValued ? _replacementValue : null,
            action == "correct" && review.IsEntityValued && _selectedCorrectionTarget is { } target ? new(target.EntityId, target.EvidenceToken) : null);
        _reviewBusy = true; _reviewMessage = null;
        try
        {
            using var response = await Http.PostAsJsonAsync($"{BaseUrl}/claims/{review.ClaimId}/review", _pendingReview);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _pendingReview = null; _review = null;
                _reviewMessage = "The claim or its evidence changed. Load the current review before deciding again.";
                return;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                _pendingReview = null; _review = null;
                _reviewMessage = "The claim is unavailable or your review permission changed.";
                return;
            }
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                _pendingReview = null; _reviewMessage = "The correction could not be accepted. Check the value and try again."; return;
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ReviewMemoryClaimResponse>() ?? throw new System.Text.Json.JsonException();
            _pendingReview = null; _review = null; _replacementValue = null;
            _reviewMessage = result?.WasReplay == true ? "This review was already recorded. Refresh to see the current state." : "Your review was recorded.";
            if (result is not null) _selected = await Http.GetFromJsonAsync<AgentMemoryItemResponse>($"{BaseUrl}/items/{result.ResultClaimId}");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _reviewMessage = _pendingReview is null ? "The review was recorded, but its details could not be refreshed. Reload the page."
                : "The result could not be confirmed. Retry the same review to check it without applying it twice.";
        }
        finally { _reviewBusy = false; }
    }
}
