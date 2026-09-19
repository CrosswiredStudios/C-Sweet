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
        Assert.Contains("Create repository", html);
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
    public async Task RepositoryDetailMapsCSweetCapabilitiesIntoFocusedTabs()
    {
        var api = new WorkspaceApi();
        var connection = Connection("InternalGit");
        var repository = Repository(connection.Id, "orbit-game", "Ready");
        api.RepositoryDetails = new(repository, 1, new("main",
            [new("refs/heads/main", "1234567890abcdef")],
            [new("1234567890abcdef", "C-Sweet", "Initialize repository")],
            ["README.md", "src/Game.cs"]));

        var html = await RenderAsync<InternalRepositoryDetail>(api, new()
        {
            [nameof(InternalRepositoryDetail.OrganizationId)] = api.Business,
            [nameof(InternalRepositoryDetail.RepositoryId)] = repository.Id,
            [nameof(InternalRepositoryDetail.CanManage)] = false
        });

        Assert.Contains("Repository sections", html);
        Assert.Contains(">Code</button>", html);
        Assert.Contains(">Changes</button>", html);
        Assert.Contains(">Branches</button>", html);
        Assert.Contains(">Access</button>", html);
        Assert.DoesNotContain(">Settings</button>", html);
        Assert.Contains("README.md", html);
        Assert.Contains("src", html);
        Assert.Contains("Clone &amp; access", html);
        Assert.DoesNotContain("New file", html);
        Assert.DoesNotContain("Star", html);
        Assert.DoesNotContain("Fork", html);
    }

    [Fact]
    public async Task RepositoryDetailShowsLatestWorkBranchWhenDefaultBranchHasNoFiles()
    {
        var api = new WorkspaceApi();
        var connection = Connection("InternalGit");
        var repository = Repository(connection.Id, "tetris", "Ready");
        const string workBranch = "csweet/tetris-build";
        api.RepositoryDetails = new(repository, 1, new("main",
            [new("refs/heads/main", "1111111111111111"), new($"refs/heads/{workBranch}", "2222222222222222")],
            [new("1111111111111111", "C-Sweet", "Initialize repository")], []));
        api.WorkBranchDetails = new(repository, 1, new("main",
            [new("refs/heads/main", "1111111111111111"), new($"refs/heads/{workBranch}", "2222222222222222")],
            [new("2222222222222222", "Daniel Kim", "Build game")], ["index.html", "main.js"]));
        api.Proposals = [new(Guid.NewGuid(), repository.Id, "2222222222222222", workBranch, "main", "AwaitingValidation", DateTimeOffset.UtcNow, false)];

        var html = await RenderAsync<InternalRepositoryDetail>(api, new()
        {
            [nameof(InternalRepositoryDetail.OrganizationId)] = api.Business,
            [nameof(InternalRepositoryDetail.RepositoryId)] = repository.Id,
            [nameof(InternalRepositoryDetail.CanManage)] = false
        });

        Assert.Contains(workBranch, html);
        Assert.Contains("Showing the latest work branch", html);
        Assert.Contains("index.html", html);
        Assert.Contains("main.js", html);
        Assert.Contains(api.Requests, request => request.Contains("reference=refs%2Fheads%2Fcsweet%2Ftetris-build", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public async Task UnfinishedOnboardingOpensConnections()
    {
        var api = new WorkspaceApi { Onboarding = new(Guid.NewGuid(), null, "ExistingGitHub", "Pending", "authorize-source-access", DateTimeOffset.UtcNow.AddHours(1)) };
        var html = await RenderAsync<SourceControl>(api, new() { [nameof(SourceControl.OrganizationId)] = api.Business });
        Assert.Matches("source-tab active[^>]*aria-current=\"page\"[^>]*>Connections", html);
        Assert.Contains("Source-control setup is not finished", html);
    }

    [Fact]
    public async Task RepositoryListShowsCreatedAndLastEventSubtitles()
    {
        var api = new WorkspaceApi();
        var connection = Connection("InternalGit");
        var created = DateTimeOffset.UtcNow.AddDays(-3);
        var occurred = DateTimeOffset.UtcNow.AddHours(-2);
        var repository = Repository(connection.Id, "orbit-game", "Ready") with
        {
            CreatedAt = created,
            LastEventType = "SourceControl.Repository.Ref",
            LastEventOccurredAt = occurred,
            LastEventActor = "Daniel Kim",
            LastEventOutcome = "Completed"
        };
        var quiet = Repository(connection.Id, "quiet-game", "Ready") with { CreatedAt = created };
        api.Repositories = [repository, quiet];
        var html = await RenderAsync<InternalRepositoryManager>(api, new()
        {
            [nameof(InternalRepositoryManager.OrganizationId)] = api.Business,
            [nameof(InternalRepositoryManager.Repositories)] = Array.Empty<SourceControlRepositorySummary>(),
            [nameof(InternalRepositoryManager.Connections)] = new[] { connection }
        });

        Assert.Contains(created.ToLocalTime().ToString("MMM d, yyyy"), html);
        Assert.Contains("Updated a branch or tag", html);
        Assert.Contains("Daniel Kim", html);
        Assert.Contains("2 hours ago", html);
        Assert.Contains("No activity yet", html);
    }

    [Fact]
    public async Task DirectoryShowsProjectsAndAgentOnlyCountsInActivityOrder()
    {
        var api = new WorkspaceApi();
        var connection = Connection("InternalGit");
        var project = new RepositoryProjectSummary(Guid.NewGuid(), "Customer portal");
        var recent = Repository(connection.Id, "z-recent", "Ready") with
        {
            LastEventOccurredAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastEventType = "SourceControl.Publication.Merged",
            Projects = [project],
            Access = [new(Guid.NewGuid(), "Developer", "Agent", "Engineering", "Team access", "Delivery"),
                new(Guid.NewGuid(), "Owner", "Human", null, "Admin", "Business membership")]
        };
        var quiet = Repository(connection.Id, "a-quiet", "Ready") with { Access = [] };
        var html = await RenderAsync<RepositoryDirectory>(api, new()
        {
            [nameof(RepositoryDirectory.OrganizationId)] = api.Business,
            [nameof(RepositoryDirectory.Repositories)] = new[] { quiet, recent },
            [nameof(RepositoryDirectory.Connections)] = new[] { connection }
        });
        Assert.Contains("Customer portal", html);
        Assert.Contains($"/projects/{project.Id:D}", html);
        Assert.Contains("1 agent has team access to z-recent", html);
        Assert.Contains("0 agents have team access to a-quiet", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.DoesNotContain("Who has access", html);
        Assert.Contains("Not assigned", html);
        Assert.True(html.IndexOf("z-recent", StringComparison.Ordinal) < html.IndexOf("a-quiet", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1, "Just now")]
    [InlineData(0, "Just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(120, "2 hours ago")]
    [InlineData(1440, "1 day ago")]
    public void ActivityTimeHasReadableUnits(int minutes, string expected)
    {
        var now = DateTimeOffset.Parse("2026-09-18T12:00:00Z");
        Assert.Equal(expected, RepositoryDirectoryPresentation.RelativeTime(now.AddMinutes(-minutes), now));
    }

    private static SourceControlConnectionSummary Connection(string provider) => new(Guid.NewGuid(), provider, provider, provider, "studio", "Business", "Ready", true, false, 1, null, null, 1);
    private static SourceControlRepositorySummary Repository(Guid connection, string name, string status) => new(Guid.NewGuid(), connection, name, "studio/" + name, "main", status, true, false, null, null, DateTimeOffset.UtcNow);

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
        public InternalRepositoryDetails? RepositoryDetails { get; set; }
        public InternalRepositoryDetails? WorkBranchDetails { get; set; }
        public IReadOnlyList<InternalGitProposalSummary> Proposals { get; set; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri.PathAndQuery);
            object body = path.EndsWith("/dashboard")
                ? new SourceControlDashboardResponse([], Repositories, Onboarding, new(false, false, null), true)
                : path.EndsWith("/internal/repositories") ? Repositories
                : path.EndsWith("/team") ? Array.Empty<InternalGitTeamAccess>()
                : path.EndsWith("/proposals") ? Proposals
                : WorkBranchDetails is not null && path.EndsWith("/" + WorkBranchDetails.Repository.Id.ToString("D")) && request.RequestUri.Query.Contains("reference=", StringComparison.OrdinalIgnoreCase) ? WorkBranchDetails
                : RepositoryDetails is not null && path.EndsWith("/" + RepositoryDetails.Repository.Id.ToString("D")) ? RepositoryDetails
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
