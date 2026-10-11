using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class GitHubPlatformWizardFlowTests
{
    [Fact]
    public async Task PersonalSetupDefaultsToNoOrganization()
    {
        var api = new WizardApi(State("organization"));
        var flow = Create(api, new(new(false, false, null), null));
        var manifest = await flow.PrepareAsync("https://csweet.test");
        Assert.Equal("/settings/apps/new", new Uri(manifest.PostUrl).AbsolutePath);
        Assert.Empty(flow.Setup.Session!.GitHubOrganization);
    }
    [Fact]
    public async Task OneContinuePreparesManifestUsingEachReturnedRevision()
    {
        var api = new WizardApi(State("organization"));
        var flow = Create(api, new(new(false, false, null), null));
        var manifest = await flow.PrepareAsync("https://csweet.test", "my-org");

        Assert.Equal("https://github.com/organizations/my-org/settings/apps/new", manifest.PostUrl);
        Assert.Equal(new[] { "sessions", "organization", "review", "manifest", "" }, api.Actions);
        Assert.Equal(new long[] { 1, 2 }, api.Revisions);
        Assert.Equal("AwaitingGitHub", flow.Setup.Session!.Status);
        Assert.Equal(4, flow.Setup.Session.Revision);
    }

    [Fact]
    public async Task VerifiedCallbackConfirmsAndActivatesWithoutProvisioner()
    {
        var api = new WizardApi(State("source-access-confirm", revision: 8));
        var flow = Create(api, api.Setup);
        await flow.FinishAsync();

        Assert.Equal(new[] { "confirm", "provisioner-choice", "activate" }, api.Actions);
        Assert.Equal(new long[] { 8, 9, 10 }, api.Revisions);
        Assert.False(api.Setup.Session!.ProvisionerRequested);
        Assert.True(flow.Setup.Readiness.ExistingGitHubAvailable);
        Assert.False(flow.Setup.Readiness.ManagedGitHubAvailable);
    }

    [Fact]
    public async Task CreationRequiresExplicitChoiceAndSeparateManifest()
    {
        var api = new WizardApi(State("complete", status: "Active") with { Readiness = new(true, false, null) });
        var flow = Create(api, api.Setup);
        await flow.PrepareAsync("https://csweet.test", "my-org", enableCreation: true);

        Assert.True(flow.Setup.Session!.ProvisionerRequested);
        Assert.Equal(new[] { "sessions", "provisioner-choice", "review", "manifest", "" }, api.Actions);
        Assert.Contains(api.Paths, path => path.EndsWith("/apps/provisioner/manifest", StringComparison.Ordinal));
        Assert.DoesNotContain("activate", api.Actions);
    }

    [Fact]
    public async Task CreationChoiceSurvivesFirstHandoffAndResumesThroughSecondApproval()
    {
        var api = new WizardApi(State("organization"));
        var flow = Create(api, new(new(false, false, null), null));
        await flow.PrepareAsync("https://csweet.test", enableCreation: true);
        Assert.True(flow.Setup.Session!.ProvisionerRequested);
        api.CompleteCallback("source-access-confirm");

        // A fresh instance represents returning from GitHub after full-page navigation.
        flow = Create(api, api.Setup);
        Assert.NotNull(await flow.FinishAsync());
        Assert.Contains(api.Paths, path => path.EndsWith("/apps/provisioner/manifest", StringComparison.Ordinal));
        Assert.DoesNotContain("activate", api.Actions);
        api.CompleteCallback("provisioner-confirm");
        flow = Create(api, api.Setup);
        Assert.Null(await flow.FinishAsync());
        Assert.True(flow.Setup.Readiness.ManagedGitHubAvailable);
        Assert.Equal(1, api.Actions.Count(action => action == "activate"));
    }

    [Fact]
    public async Task ActivationFailureRetainsProgressAndRetryDoesNotRepeatConfirmation()
    {
        var api = new WizardApi(State("source-access-confirm")) { FailActivation = true };
        var flow = Create(api, api.Setup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.FinishAsync());
        Assert.Equal("review-activate", flow.Setup.Session!.CurrentStep);
        api.FailActivation = false;
        await flow.FinishAsync();
        Assert.Equal(1, api.Actions.Count(a => a == "confirm"));
        Assert.Equal(2, api.Actions.Count(a => a == "activate"));
        Assert.True(flow.Setup.Readiness.ExistingGitHubAvailable);
    }

    [Fact]
    public async Task UnverifiedHandoffCannotBeActivatedByWizard()
    {
        var api = new WizardApi(State("source-access-review", status: "AwaitingGitHub"));
        await Create(api, api.Setup).FinishAsync();
        Assert.Empty(api.Actions);
    }

    private static GitHubPlatformWizardFlow Create(WizardApi api, PlatformSourceControlSetupResponse state) =>
        new(new HttpClient(api) { BaseAddress = new Uri("https://csweet.test/") }, state, _ => Task.CompletedTask);

    private static PlatformSourceControlSetupResponse State(string step, string status = "InProgress", long revision = 1) =>
        new(new(false, false, null), new(Guid.NewGuid(), status, step, "my-org", "https://csweet.test",
            true, false, step != "organization", null, false, false, false, null, null, null,
            DateTimeOffset.UtcNow.AddHours(24), revision));

    private sealed class WizardApi(PlatformSourceControlSetupResponse initial) : HttpMessageHandler
    {
        public PlatformSourceControlSetupResponse Setup { get; private set; } = initial;
        public List<string> Actions { get; } = [];
        public List<string> Paths { get; } = [];
        public List<long> Revisions { get; } = [];
        public bool FailActivation { get; set; }
        public void CompleteCallback(string step) => Setup = Setup with
        {
            Session = Setup.Session! with { CurrentStep = step, Status = "InProgress", Revision = Setup.Session.Revision + 1 }
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var action = path.Split('/').Last();
            Actions.Add(action); Paths.Add(path);
            JsonElement body = default;
            if (request.Content is not null)
                body = await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("expectedRevision", out var revision))
            {
                Assert.Equal(Setup.Session!.Revision, revision.GetInt64());
                Revisions.Add(revision.GetInt64());
            }
            if (action == "activate" && FailActivation)
                return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { message = "Retry activation" }) };
            var session = Setup.Session!;
            session = action switch
            {
                "sessions" => session with { ProvisionerRequested = body.GetProperty("allowRepositoryCreation").GetBoolean(), CurrentStep = Setup.Readiness.ExistingGitHubAvailable ? "capabilities" : "organization", Status = "InProgress", Revision = 1 },
                "organization" => session with { GitHubOrganization = body.GetProperty("organizationLogin").GetString() ?? "", CurrentStep = "source-access-review", Revision = session.Revision + 1 },
                "review" => session with { SourceAccessPermissionsConfirmed = true, ProvisionerPermissionsConfirmed = true, Revision = session.Revision + 1 },
                "manifest" => session with { Status = "AwaitingGitHub", Revision = session.Revision + 1 },
                "confirm" => session with { CurrentStep = path.Contains("provisioner/") ? "review-activate" : "capabilities", SourceAccessAppConfirmed = true, Revision = session.Revision + 1 },
                "provisioner-choice" => session with { ProvisionerRequested = body.GetProperty("enableProvisioner").GetBoolean(), CurrentStep = body.GetProperty("enableProvisioner").GetBoolean() ? "provisioner-review" : "review-activate", Revision = session.Revision + 1 },
                "activate" => session with { Status = "Active", CurrentStep = "complete", Revision = session.Revision + 1 },
                _ => session
            };
            Setup = Setup with { Session = session };
            if (action == "activate") Setup = Setup with { Readiness = new(true, session.ProvisionerRequested == true, null) };
            object result = action == "manifest"
                ? new PlatformGitHubManifestLaunchResponse(string.IsNullOrEmpty(session.GitHubOrganization) ? "https://github.com/settings/apps/new" : $"https://github.com/organizations/{session.GitHubOrganization}/settings/apps/new", "{}", DateTimeOffset.UtcNow.AddMinutes(20))
                : Setup;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
}
