using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.SourceControl;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class RepositoryGitHubConnectionFlowTests
{
    [Fact]
    public async Task ConfiguredGitHubGoesStraightToDraftAuthorization()
    {
        var api = new Api(HttpStatusCode.OK);
        var result = await Flow(api).ConnectAsync(api.DraftId);
        Assert.Equal("https://github.com/apps/csweet/installations/new?state=draft", result.Authorization!.AuthorizationUrl);
        Assert.Null(result.Setup);
        Assert.Single(api.Requests);
        Assert.Equal($"/api/repository-onboarding/{api.DraftId:D}/github/authorize", api.Requests[0]);
    }

    [Fact]
    public async Task UnconfiguredGitHubReturnsSetupForTheSameWizard()
    {
        var api = new Api(HttpStatusCode.Conflict);
        var result = await Flow(api).ConnectAsync(api.DraftId);
        Assert.Null(result.Authorization);
        Assert.False(result.Setup!.Readiness.ExistingGitHubAvailable);
        Assert.Equal("/api/source-control/platform-setup/", api.Requests[1]);
    }

    [Fact]
    public async Task NonAdministratorGetsActionableMessageWithoutNavigatingToSettings()
    {
        var api = new Api(HttpStatusCode.Conflict) { SetupStatus = HttpStatusCode.Forbidden };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Flow(api).ConnectAsync(api.DraftId));
        Assert.Contains("administrator", error.Message);
        Assert.Contains("draft is saved", error.Message);
    }

    [Fact]
    public async Task OperatorManagedSetupDoesNotOfferAppRegistration()
    {
        var api = new Api(HttpStatusCode.Conflict) { Setup = new(new(false, false, null, "ExternallyManaged"), null) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Flow(api).ConnectAsync(api.DraftId));
        Assert.Contains("operator-managed", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DraftAuthorizationFailuresDoNotStartPlatformSetup(HttpStatusCode status)
    {
        var api = new Api(status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Flow(api).ConnectAsync(api.DraftId));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task ConflictWithConfiguredAppDoesNotRecreateSetup()
    {
        var api = new Api(HttpStatusCode.Conflict) { Setup = new(new(true, false, null), null) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Flow(api).ConnectAsync(api.DraftId));
        Assert.Equal("Draft cannot be changed", error.Message);
    }

    private static RepositoryGitHubConnectionFlow Flow(Api api) => new(new HttpClient(api) { BaseAddress = new("https://csweet.test/") });

    private sealed class Api(HttpStatusCode authorizationStatus) : HttpMessageHandler
    {
        public Guid DraftId { get; } = Guid.NewGuid();
        public List<string> Requests { get; } = [];
        public HttpStatusCode SetupStatus { get; init; } = HttpStatusCode.OK;
        public PlatformSourceControlSetupResponse Setup { get; init; } = new(new(false, false, null), null);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/github/authorize"))
                return Task.FromResult(new HttpResponseMessage(authorizationStatus) { Content = authorizationStatus == HttpStatusCode.OK
                    ? JsonContent.Create(new DraftGitHubAuthorization("https://github.com/apps/csweet/installations/new?state=draft", "draft"))
                    : JsonContent.Create(new { message = "Draft cannot be changed" }) });
            return Task.FromResult(new HttpResponseMessage(SetupStatus) { Content = JsonContent.Create(Setup) });
        }
    }
}
