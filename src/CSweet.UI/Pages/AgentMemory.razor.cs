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
            { _review = null; _reviewMessage = L["AgentMemory.ThisClaimIsUnavailableOr"]; return; }
            response.EnsureSuccessStatusCode();
            _correctionTargets = await response.Content.ReadFromJsonAsync<MemoryClaimCorrectionTarget[]>() ?? [];
            if (_correctionTargets.Count == 0) _reviewMessage = L["AgentMemory.NoEligibleEntitiesMatchedTry"];
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { _reviewMessage = L["AgentMemory.EntityChoicesCouldNotBe"]; }
        finally { _reviewBusy = false; }
    }

    private async Task InspectReviewSourceAsync(Guid sourceId)
    {
        if (SelectionLocked) return;
        _reviewBusy = true;
        try
        {
            var item = await Http.GetFromJsonAsync<AgentMemoryItemResponse>($"{BaseUrl}/items/{sourceId}");
            if (item is null) { _reviewMessage = L["AgentMemory.ThisSourceIsNoLonger"]; return; }
            _selected = item; _review = null; _reviewMessage = null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { _reviewMessage = L["AgentMemory.ThisSourceCouldNotBe"]; }
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
                _reviewMessage = L["AgentMemory.ReviewRequiresAnAuthorizedHuman"];
                return;
            }
            if (!response.IsSuccessStatusCode) { _reviewMessage = L["AgentMemory.ThisClaimCannotCurrentlyBe"]; return; }
            var review = await response.Content.ReadFromJsonAsync<MemoryClaimReviewResponse>();
            if (_selected?.Id == selected.Id) _review = review;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { _reviewMessage = L["AgentMemory.ReviewDetailsCouldNotBe"]; }
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
                _reviewMessage = L["AgentMemory.TheClaimOrItsEvidence"];
                return;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                _pendingReview = null; _review = null;
                _reviewMessage = L["AgentMemory.TheClaimIsUnavailableOr"];
                return;
            }
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                _pendingReview = null; _reviewMessage = L["AgentMemory.TheCorrectionCouldNotBe"]; return;
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ReviewMemoryClaimResponse>() ?? throw new System.Text.Json.JsonException();
            _pendingReview = null; _review = null; _replacementValue = null;
            _reviewMessage = result?.WasReplay == true ? "This review was already recorded. Refresh to see the current state." : "Your review was recorded.";
            if (result is not null)
            {
                var previous = _selected?.Id;
                _selected = await Http.GetFromJsonAsync<AgentMemoryItemResponse>($"{BaseUrl}/items/{result.ResultClaimId}");
                await RefreshSelectedRecordAsync(previous);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _reviewMessage = _pendingReview is null ? "The review was recorded, but its details could not be refreshed. Reload the page."
                : "The result could not be confirmed. Retry the same review to check it without applying it twice.";
        }
        finally { _reviewBusy = false; }
    }
}
