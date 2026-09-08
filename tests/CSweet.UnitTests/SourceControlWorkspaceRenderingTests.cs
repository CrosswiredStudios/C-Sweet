using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.SourceControl;
using CSweet.UI.Components;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class SourceControlWorkspaceRenderingTests
{
    [Fact]
    public async Task LandingPageLoadsRepositoriesWithoutLoadingAdministration()
    {
        var api = new WorkspaceApi();
        var html = await RenderAsync<SourceControl>(api, new() { [nameof(SourceControl.OrganizationId)] = api.Business });

        Assert.Contains("A home for your team", html);
        Assert.Contains("New repository", html);
        Assert.Contains("Connections</button>", html);
        Assert.Contains("Activity</button>", html);
        Assert.Contains("Settings</button>", html);
        Assert.DoesNotContain("Save default", html);
        Assert.DoesNotContain("Coming next", html);
        Assert.DoesNotContain("Repository name", html);
        Assert.DoesNotContain(api.Requests, p => p.Contains("backups") || p.Contains("provisioning") || p.Contains("/activity"));
    }

    [Fact]
    public async Task UnifiedListIncludesExternalAndArchivedRepositoriesWithoutDuplicates()
    {
        var api = new WorkspaceApi();
        var internalConnection = Connection("InternalGit");
        var externalConnection = Connection("GitHub");
        var archived = Repository(internalConnection.Id, "archived-game", "Archived");
        var external = Repository(externalConnection.Id, "connected-game", "AttentionRequired");
        api.Repositories = [archived];
        var html = await RenderAsync<InternalRepositoryManager>(api, new()
        {
            [nameof(InternalRepositoryManager.OrganizationId)] = api.Business,
            [nameof(InternalRepositoryManager.Repositories)] = new[] { archived, external },
            [nameof(InternalRepositoryManager.Connections)] = new[] { internalConnection, externalConnection }
        });

        Assert.Contains("2 repositories", html);
        Assert.Contains("archived-game", html);
        Assert.Contains("connected-game", html);
        Assert.Contains("C-Sweet storage", html);
        Assert.Contains("GitHub", html);
        Assert.Contains("Archived", html);
        Assert.Contains("Needs attention", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreationFormRequiresManagementPermission(bool canManage)
    {
        var api = new WorkspaceApi();
        var html = await RenderAsync<InternalRepositoryManager>(api, new()
        {
            [nameof(InternalRepositoryManager.OrganizationId)] = api.Business,
            [nameof(InternalRepositoryManager.CanManage)] = canManage,
            [nameof(InternalRepositoryManager.Creating)] = true
        });
        Assert.Equal(canManage, html.Contains("New private repository"));
        Assert.Equal(canManage, html.Contains("Repository name"));
    }

    [Fact]
    public async Task UnfinishedOnboardingOpensConnections()
    {
        var api = new WorkspaceApi { Onboarding = new(Guid.NewGuid(), null, "ExistingGitHub", "Pending", "authorize-source-access", DateTimeOffset.UtcNow.AddHours(1)) };
        var html = await RenderAsync<SourceControl>(api, new() { [nameof(SourceControl.OrganizationId)] = api.Business });
        Assert.Matches("source-tab active[^>]*aria-current=\"page\"[^>]*>Connections", html);
        Assert.Contains("Source-control setup is not finished", html);
    }

    private static SourceControlConnectionSummary Connection(string provider) => new(Guid.NewGuid(), provider, provider, provider, "studio", "Business", "Ready", true, false, 1, null, null, 1);
    private static SourceControlRepositorySummary Repository(Guid connection, string name, string status) => new(Guid.NewGuid(), connection, name, "studio/" + name, "main", status, true, false, null, null);

    private static async Task<string> RenderAsync<T>(WorkspaceApi api, Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("https://host.example.com/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    private sealed class WorkspaceApi : HttpMessageHandler
    {
        public Guid Business { get; } = Guid.NewGuid();
        public List<string> Requests { get; } = [];
        public IReadOnlyList<SourceControlRepositorySummary> Repositories { get; set; } = [];
        public SourceControlOnboardingSummary? Onboarding { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            object body = path.EndsWith("/dashboard")
                ? new SourceControlDashboardResponse([], Repositories, Onboarding, new(false, false, null), true)
                : path.EndsWith("/internal/repositories") ? Repositories
                : path.EndsWith("/teams") ? new { Teams = Array.Empty<object>() }
                : throw new InvalidOperationException("Unexpected request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://host.example.com/", "https://host.example.com/source-control");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
