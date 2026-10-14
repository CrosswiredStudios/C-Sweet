using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.PersonalAssistant;

namespace CSweet.UI.Services;

public sealed class PersonalAssistantApiClient(HttpClient http)
{
    public Task<PersonalAssistantSettings> SettingsAsync() => GetAsync<PersonalAssistantSettings>("api/personal-assistant/settings");
    public Task<PersonalAssistantSettings> ConfigureAsync(ConfigurePersonalAssistantRequest request) => SendAsync<PersonalAssistantSettings>(HttpMethod.Put, "api/personal-assistant/settings", request);
    public Task<IReadOnlyList<PersonalAssistantMessageResponse>> MessagesAsync(Guid? draft = null) => GetAsync<IReadOnlyList<PersonalAssistantMessageResponse>>("api/personal-assistant/messages" + (draft.HasValue ? $"?draftId={draft:D}" : ""));
    public Task<PersonalAssistantMessageResponse> MessageAsync(SendPersonalAssistantMessageRequest request) => SendAsync<PersonalAssistantMessageResponse>(HttpMethod.Post, "api/personal-assistant/messages", request);
    public Task<IReadOnlyList<RepositoryOnboardingDraftResponse>> DraftsAsync() => GetAsync<IReadOnlyList<RepositoryOnboardingDraftResponse>>("api/repository-onboarding/");
    public Task<RepositoryOnboardingDraftResponse> CreateDraftAsync() => SendAsync<RepositoryOnboardingDraftResponse>(HttpMethod.Post, "api/repository-onboarding/", null);
    public Task<RepositoryOnboardingDraftResponse> DraftAsync(Guid id) => GetAsync<RepositoryOnboardingDraftResponse>($"api/repository-onboarding/{id:D}");
    public Task<T> GetAsync<T>(string path) => ReadAsync<T>(http.GetAsync(path));
    public Task<T> SendAsync<T>(HttpMethod method, string path, object? body) => ReadAsync<T>(http.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }));
    public Task<RepositoryOnboardingDraftResponse> UploadAsync(Guid id, long revision, string name, Stream content) => ReadAsync<RepositoryOnboardingDraftResponse>(
        http.PostAsync($"api/repository-onboarding/{id:D}/upload?revision={revision}&fileName={Uri.EscapeDataString(name)}", new StreamContent(content)));
    private static async Task<T> ReadAsync<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var text = $"The request could not finish ({(int)response.StatusCode}). Please try again.";
            try
            {
                using var json = JsonDocument.Parse(body);
                foreach (var key in new[] { "message", "detail", "title" })
                    if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    { text = value.GetString()!; break; }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? $"The request could not finish ({(int)response.StatusCode})." : text);
        }
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("The server returned no result.");
    }
}
