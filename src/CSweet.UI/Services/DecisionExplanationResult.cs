using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Communications;

namespace CSweet.UI.Services;

internal sealed record DecisionExplanationResult(string? Answer, string? ErrorKey)
{
    public static async Task<DecisionExplanationResult> RequestAsync(HttpClient http, Guid organizationId, Guid conversationId,
        Guid decisionId, DecisionQuestionRequest request, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"api/organizations/{organizationId}/communications/hub/chats/{conversationId}/decisions/{decisionId}/explain", request, token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "Communications.Decision.Unavailable");
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<DecisionExplanationResponse>(token);
            if (!string.IsNullOrWhiteSpace(result?.Explanation)) return new(result.Explanation, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // The user can retry the explanation while the decision stays pending.
        }
        return new(null, "Communications.Decision.ExplanationFailed");
    }
}
