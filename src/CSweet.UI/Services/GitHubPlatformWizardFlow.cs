using System.Net;
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
        if (Setup.Session is not { } original) return null;
        // Callbacks and another open wizard can advance the saved session while this
        // component is showing an older response. Resume state, never replay stale requests.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await RefreshCompletionAsync(original);
            if (Setup.Session!.Status == "Active") return null;
            try { return await FinishCurrentAsync(); }
            catch (SetupRevisionConflictException)
            {
                if (attempt == 0) continue;
                await RefreshCompletionAsync(original);
                throw new InvalidOperationException("GitHub setup changed again while finishing. Your saved app is retained. Retry to continue from the latest step.");
            }
        }
        return null;
    }

    private async Task RefreshCompletionAsync(PlatformSourceControlSetupSessionResponse original)
    {
        using var response = await http.GetAsync("api/source-control/platform-setup/");
        await EnsureSuccessAsync(response);
        Setup = await response.Content.ReadFromJsonAsync<PlatformSourceControlSetupResponse>()
            ?? throw new InvalidOperationException("C-Sweet returned no setup progress.");
        await changed(Setup);
        if (Setup.Session is not { } current || current.SessionId != original.SessionId ||
            current.Status is "Cancelled" or "Expired")
            throw new InvalidOperationException("This GitHub setup is no longer current. Reopen GitHub setup to review its saved state.");
        if ((current.ProvisionerRequested == true) != (original.ProvisionerRequested == true))
            throw new InvalidOperationException("Repository creation permissions changed. Reopen GitHub setup to review the current choice before continuing.");
    }

    private async Task<PlatformGitHubManifestLaunchResponse?> FinishCurrentAsync()
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
            if (response.StatusCode == HttpStatusCode.Conflict &&
                json.RootElement.TryGetProperty("error", out var error) && error.GetString() == "revision_conflict")
                throw new SetupRevisionConflictException();
            if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException(message.GetString());
        }
        catch (JsonException) { }
        throw new InvalidOperationException("C-Sweet could not complete GitHub setup. Try again.");
    }

    private sealed class SetupRevisionConflictException() : InvalidOperationException("The setup changed. Refresh before continuing.");
}
