using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.SourceControl;

namespace CSweet.UI.Services;

/// <summary>Keeps platform setup and draft-scoped account authorization in the import flow.</summary>
public sealed class RepositoryGitHubConnectionFlow(HttpClient http)
{
    public async Task<(DraftGitHubAuthorization? Authorization, PlatformSourceControlSetupResponse? Setup)> ConnectAsync(Guid draftId)
    {
        using var response = await http.PostAsync($"api/repository-onboarding/{draftId:D}/github/authorize", null);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var setup = await LoadSetupAsync();
            if (!setup.Readiness.ExistingGitHubAvailable)
            {
                if (setup.Readiness.ConfigurationMode == "ExternallyManaged")
                    throw new InvalidOperationException("Your C-Sweet administrator needs to finish the operator-managed GitHub configuration. Your business draft is saved.");
                return (null, setup);
            }
        }
        await GitHubPlatformWizardFlow.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<DraftGitHubAuthorization>()
            ?? throw new InvalidOperationException("C-Sweet returned no GitHub authorization link."), null);
    }

    public async Task<PlatformSourceControlSetupResponse> LoadSetupAsync()
    {
        using var response = await http.GetAsync("api/source-control/platform-setup/");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("A C-Sweet administrator needs to set up GitHub before you can connect it. Your business draft is saved.");
        await GitHubPlatformWizardFlow.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<PlatformSourceControlSetupResponse>()
            ?? throw new InvalidOperationException("C-Sweet returned no GitHub setup progress.");
    }
}
