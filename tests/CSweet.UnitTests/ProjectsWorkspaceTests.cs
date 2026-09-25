using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.Projects;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class ProjectsWorkspaceTests
{
    [Theory]
    [InlineData("teams", "people")]
    [InlineData("governance", "decisions")]
    [InlineData("evidence", "delivery")]
    [InlineData("invalid", "overview")]
    public void ExistingSectionLinksRemainSupported(string oldTab, string current) => Assert.Equal(current, ProjectPresentation.Tab(oldTab));

    [Fact]
    public async Task PortfolioShowsAuthorizedNamesRealBlockersAndReadiness()
    {
        var api = new Api();
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId });
        Assert.Contains("Neon Bash", html);
        Assert.Contains("Matt", html);
        Assert.Contains("2 blocked", html);
        Assert.Contains("Not ready", html);
        Assert.DoesNotContain("v@context", html);
        Assert.DoesNotContain("Manager ID", html);
        Assert.All(api.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData("overview", "Project brief")]
    [InlineData("people", "Supervision")]
    [InlineData("documents", "Reviews")]
    [InlineData("communications", "No communications yet")]
    [InlineData("decisions", "Authority")]
    [InlineData("delivery", "Evaluations")]
    [InlineData("audit", "Audit timeline")]
    public async Task ProjectSectionsRenderTheirOwnContent(string tab, string expected)
    {
        var api = new Api();
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId, ["Tab"] = tab });
        Assert.Contains(expected, html);
        Assert.Contains("Neon Bash", html);
        Assert.DoesNotContain("New project", html);
        Assert.All(api.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData("boards", "All boards")]
    [InlineData("personal", "Personal boards")]
    public async Task DirectoriesDoNotRequireProjectInspectionAccess(string view, string expected)
    {
        var api = new Api();
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["View"] = view });
        Assert.Contains(expected, html);
        Assert.DoesNotContain(api.Paths, path => path.Contains("/inspection"));
        if (view == "personal") Assert.DoesNotContain(api.Paths, path => path.Contains("/work/boards"));
    }

    [Fact]
    public void MetadataAcceptsBothWireAndStoredPropertyCasing()
    {
        var resource = Resource() with { Metadata = JsonSerializer.SerializeToElement(new { outcome = "Ship a prototype" }) };
        Assert.Equal("Ship a prototype", ProjectPresentation.Metadata(resource, "Outcome"));
        Assert.Equal("Not set", ProjectPresentation.Metadata(resource, "TargetDate"));
    }

    private static ProjectInspectionResource Resource() => new(Guid.NewGuid(), "Workstream", "software-prototype.v1", "Neon Bash", "Active", 1, null, null, null, null, null, DateTimeOffset.UtcNow, "/projects", JsonSerializer.SerializeToElement(new { Outcome = "Ship a prototype", LifecycleStage = "Development" }));

    private static async Task<string> Render(Api api, Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Projects>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class Api : HttpMessageHandler
    {
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public List<string> Paths { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path); Methods.Add(request.Method);
            object result;
            if (path.EndsWith($"/{ProjectId}/inspection")) result = new ProjectInspectionResponse(Resource(), new(0, 0, 0, 0, 0, 0, 0, false), [], [], [], [], [], [], [], DateTimeOffset.UtcNow);
            else if (path.EndsWith("/workstreams/inspection")) result = new ProjectPortfolioResponse(DateTimeOffset.UtcNow, 1, 1,
                [new(ProjectId, "Neon Bash", "Ship a prototype", "Active", "Development", "software-prototype.v1", 1, Guid.NewGuid(), null, null, null, 1, DateTimeOffset.UtcNow, 1, 1, 5, 0, 0, null) { AccountableManagerName = "Matt", BlockedItems = 2, ReleaseReady = false }]);
            else if (path.EndsWith("/work/boards")) result = new WorkBoardDirectoryResponse([], false);
            else if (path.EndsWith("/work/personal-todos")) result = new CSweet.WorkManagement.Contracts.PersonalTodoDirectory([]);
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/projects");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
