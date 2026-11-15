using System.Net.Http.Json;
using CSweet.Contracts.Auth;

namespace CSweet.UI.Services;

/// <summary>Settings → C-Sweet account (IL-11): link status and the link flow for an existing installation.</summary>
public sealed class InstallationLinkApiClient(HttpClient http)
{
    public async Task<InstallationLinkStatusResponse?> GetAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<InstallationLinkStatusResponse>("api/installation/link", ct);

    public async Task<HostedSetupStatusResponse> StartAsync(string installationName, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/installation/link/start", new StartHostedSetupRequest(installationName), ct);
        if (!response.IsSuccessStatusCode) return new HostedSetupStatusResponse("unavailable");
        return await response.Content.ReadFromJsonAsync<HostedSetupStatusResponse>(ct) ?? new HostedSetupStatusResponse("unavailable");
    }

    public async Task<HostedSetupStatusResponse> PollAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("api/installation/link/poll", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostedSetupStatusResponse>(ct) ?? new HostedSetupStatusResponse("missing");
    }

    public async Task<AuthActionResponse> CompleteAsync(string password, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/installation/link/complete", new CompleteInstallationLinkRequest(password), ct);
        return await response.Content.ReadFromJsonAsync<AuthActionResponse>(ct)
            ?? new AuthActionResponse(false, "link_failed", "The link could not be completed.");
    }

    public async Task CancelAsync(CancellationToken ct = default)
    {
        using var _ = await http.PostAsync("api/installation/link/cancel", null, ct);
    }
}
