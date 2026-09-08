using System.Net.Http.Json;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Services;

public sealed class CalendarApiClient(HttpClient http)
{
    private static string Root(Guid org) => $"api/organizations/{org}/calendar";
    public async Task<BusinessCalendarView> ReadAsync(Guid org, DateTimeOffset from, DateTimeOffset to) =>
        await http.GetFromJsonAsync<BusinessCalendarView>($"{Root(org)}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}")
        ?? throw new InvalidOperationException("Calendar unavailable.");
    public async Task<IReadOnlyList<CalendarReminder>> RemindersAsync(Guid org) =>
        await http.GetFromJsonAsync<CalendarReminder[]>($"{Root(org)}/reminders") ?? [];
    public Task CreateAsync(Guid org, CreateCalendarEventRequest request) => Send(HttpMethod.Post, $"{Root(org)}/events", request);
    public Task UpdateAsync(Guid org, UpdateCalendarEventRequest request) => Send(HttpMethod.Put, $"{Root(org)}/events", request);
    public Task CancelAsync(Guid org, CancelCalendarEventRequest request) => Send(HttpMethod.Post, $"{Root(org)}/events/cancel", request);
    public Task SettingsAsync(Guid org, UpdateCalendarSettingsRequest request) => Send(HttpMethod.Put, $"{Root(org)}/settings", request);
    public Task MarkReadAsync(Guid org, Guid id) => Send(HttpMethod.Post, $"{Root(org)}/reminders/{id}/read", new { });
    private async Task Send<T>(HttpMethod method, string uri, T body)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync();
        try { message = JsonDocument.Parse(message).RootElement.GetProperty("message").GetString() ?? message; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { }
        throw new InvalidOperationException(message);
    }
}
