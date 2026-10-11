using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.UI.Services;

/// <summary>Combines wizard actions while retaining the server's revision and verification checks.</summary>
public sealed class GitHubPlatformWizardFlow(
    HttpClient http,
    PlatformSourceControlSetupResponse setup,
    Func<PlatformSourceControlSetupResponse, Task> changed)
{
    public PlatformSourceControlSetupResponse Setup { get; private set; } = setup;
    public static bool IsPending(PlatformSourceControlSetupResponse setup) =>
        setup.Session is { Status: not ("Active" or "Cancelled" or "Expired") };
    private string SessionUrl => $"api/source-control/platform-setup/sessions/{Setup.Session!.SessionId:D}";
    private long Revision => Setup.Session!.Revision;

    public async Task<PlatformGitHubManifestLaunchResponse> PrepareAsync(
        string baseUrl, string? organization = null, bool enableCreation = false)
    {
        if (!IsPending(Setup))
            await UpdateAsync(http.PostAsJsonAsync("api/source-control/platform-setup/sessions",
                new StartPlatformSourceControlSetupRequest(baseUrl, AllowRepositoryCreation: enableCreation)));
        if (Setup.Session!.CurrentStep == "organization")
            await UpdateAsync(http.PutAsJsonAsync($"{SessionUrl}/organization",
                new ConfirmPlatformOrganizationRequest(organization, true, Revision)));
        if (Setup.Session.CurrentStep == "capabilities")
            await UpdateAsync(http.PutAsJsonAsync($"{SessionUrl}/provisioner-choice",
                new ChoosePlatformProvisionerRequest(Setup.Session.ProvisionerRequested == true, Revision)));
        return await PrepareManifestAsync();
    }

    private async Task<PlatformGitHubManifestLaunchResponse> PrepareManifestAsync()
    {
        var kind = Setup.Session!.CurrentStep switch
        {
            "source-access-review" => "source-access",
            "provisioner-review" => "provisioner",
            _ => throw new InvalidOperationException("Resume the current setup before starting another GitHub handoff.")
        };
        var reviewed = kind == "source-access" ? Setup.Session.SourceAccessPermissionsConfirmed : Setup.Session.ProvisionerPermissionsConfirmed;
        if (!reviewed)
            await UpdateAsync(http.PutAsJsonAsync($"{SessionUrl}/apps/{kind}/review",
                new ConfirmPlatformAppReviewRequest(true, Revision)));
        using var response = await http.PostAsync($"{SessionUrl}/apps/{kind}/manifest", null);
        await EnsureSuccessAsync(response);
        var manifest = await response.Content.ReadFromJsonAsync<PlatformGitHubManifestLaunchResponse>()
            ?? throw new InvalidOperationException("C-Sweet returned no GitHub setup link.");
        // Manifest generation advances the revision; refresh before any subsequent action.
        Setup = await http.GetFromJsonAsync<PlatformSourceControlSetupResponse>("api/source-control/platform-setup/")
            ?? throw new InvalidOperationException("C-Sweet returned no setup progress.");
        await changed(Setup);
        return manifest;
    }

    public async Task<PlatformGitHubManifestLaunchResponse?> FinishAsync()
    {
        if (Setup.Session?.CurrentStep is "source-access-confirm" or "provisioner-confirm")
        {
            var kind = Setup.Session.CurrentStep == "source-access-confirm" ? "source-access" : "provisioner";
            await UpdateAsync(http.PutAsJsonAsync($"{SessionUrl}/apps/{kind}/confirm",
                new ConfirmPlatformAppRequest(true, Revision)));
        }
        if (Setup.Session?.CurrentStep == "capabilities")
            await UpdateAsync(http.PutAsJsonAsync($"{SessionUrl}/provisioner-choice",
                new ChoosePlatformProvisionerRequest(Setup.Session.ProvisionerRequested == true, Revision)));
        if (Setup.Session?.CurrentStep == "provisioner-review")
            return await PrepareManifestAsync();
        if (Setup.Session?.CurrentStep == "review-activate")
            await UpdateAsync(http.PostAsJsonAsync($"{SessionUrl}/activate",
                new ActivatePlatformSourceControlRequest(true, Revision)));
        return null;
    }

    public Task CancelAsync() => UpdateAsync(http.PostAsJsonAsync($"{SessionUrl}/cancel",
        new CancelPlatformSourceControlSetupRequest(true, Revision)));

    private async Task UpdateAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        await EnsureSuccessAsync(response);
        Setup = await response.Content.ReadFromJsonAsync<PlatformSourceControlSetupResponse>()
            ?? throw new InvalidOperationException("C-Sweet returned no setup progress.");
        await changed(Setup);
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException(message.GetString());
        }
        catch (JsonException) { }
        throw new InvalidOperationException("C-Sweet could not complete GitHub setup. Try again.");
    }
}
