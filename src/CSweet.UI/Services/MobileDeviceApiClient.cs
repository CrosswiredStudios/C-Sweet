using System.Net.Http.Json;
using CSweet.Contracts.Mobile;

namespace CSweet.UI.Services;

/// <summary>Settings → Phones: the signed-in person's phones and their approval (H4).</summary>
public sealed class MobileDeviceApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<MobileDeviceResponse>> ListAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<MobileDeviceResponse>>("api/mobile/devices", ct) ?? [];

    /// <summary><paramref name="decision"/> is approve, deny or revoke. Returns null when the phone changed meanwhile.</summary>
    public async Task<MobileDeviceResponse?> DecideAsync(Guid deviceId, string decision, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/mobile/devices/{deviceId:D}/{decision}", null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MobileDeviceResponse>(ct);
    }
}
