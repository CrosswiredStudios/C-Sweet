using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Communications;

namespace CSweet.UI.Services;

internal static class DecisionExplanationResult
{
    public static async Task<DecisionWordingResponse?> GetWordingAsync(HttpClient http, Guid organizationId, Guid conversationId,
        Guid decisionId, string? optionId, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"api/organizations/{organizationId}/communications/hub/chats/{conversationId}/decisions/{decisionId}/wording",
                new DecisionWordingRequest(optionId), token);
            if (!response.IsSuccessStatusCode) return null;
            var copy = await response.Content.ReadFromJsonAsync<DecisionWordingResponse>(token);
            return !string.IsNullOrWhiteSpace(copy?.Text) && (optionId is null || !string.IsNullOrWhiteSpace(copy.Impact)) ? copy : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

}
