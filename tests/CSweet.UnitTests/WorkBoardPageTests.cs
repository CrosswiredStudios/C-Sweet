using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkBoardPageTests
{
    [Fact]
    public async Task BoardDirectoryShowsPersonalBoardLoadFailure()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new PersonalBoardFailureApi()) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkBoards>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkBoards.OrganizationId)] = BoardApi.OrganizationId
            }));
            return component.ToHtmlString();
        });

        Assert.Contains("Personal boards", html);
        Assert.Contains("Personal boards could not be loaded", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoardPagePassesActualErrorsToWorkspace(bool sprintRequestFails)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new BoardApi(sprintRequestFails)) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkBoards>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkBoards.OrganizationId)] = BoardApi.OrganizationId,
                [nameof(WorkBoards.BoardId)] = BoardApi.BoardId
            }));
            return component.ToHtmlString();
        });

        Assert.Contains("BLAST GRID", html);
        Assert.DoesNotContain("_moveError", html);
        Assert.DoesNotContain("_sprintError", html);
        if (sprintRequestFails)
        {
            Assert.Contains("Sprint information could not be loaded", html);
            Assert.DoesNotContain("No active sprint", html);
        }
        else
        {
            Assert.Contains("No active sprint", html);
            Assert.DoesNotContain("Sprint information could not be loaded", html);
            Assert.DoesNotContain("Sprint information unavailable", html);
        }
    }

    [Fact]
    public async Task BoardDirectoryLinksPersonalBoardsToTheEmployeePersonalBoardTab()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new PersonalBoardApi()) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkBoards>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkBoards.OrganizationId)] = PersonalBoardApi.OrganizationId
            }));
            return component.ToHtmlString();
        });

        Assert.Contains(
            $"/organizations/{PersonalBoardApi.OrganizationId:D}/employees/{PersonalBoardApi.OwnerOrganizationUserId:D}?tab=personal-board",
            html);
    }

    private sealed class BoardApi(bool sprintRequestFails) : HttpMessageHandler
    {
        public static readonly Guid OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var summary = new WorkBoardSummaryResponse(BoardId, OrganizationId, null, "BLAST GRID", "Game delivery", false, false, false,
                null, 0, 0, 1, now, now, [WorkSprintActions.Read]) { Key = "GAME" };
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/boards"))
                result = new WorkBoardDirectoryResponse([summary], true);
            else if (path.EndsWith(BoardId.ToString()))
                result = new WorkBoardDetailResponse(summary, [], []);
            else if (path.EndsWith("/sprints"))
            {
                if (sprintRequestFails)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                result = Array.Empty<WorkSprintResponse>();
            }
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }

    private sealed class PersonalBoardFailureApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/work/boards"))
            {
                var directory = new WorkBoardDirectoryResponse([], true);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(directory)
                });
            }

            if (path.EndsWith("/work/personal-todos"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }

    private sealed class PersonalBoardApi : HttpMessageHandler
    {
        public static readonly Guid OrganizationId = Guid.NewGuid();
        public static readonly Guid OwnerOrganizationUserId = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/work/boards"))
                result = new WorkBoardDirectoryResponse([], true);
            else if (path.EndsWith("/work/personal-todos"))
                result = new Wire.PersonalTodoDirectory([new Wire.PersonalTodoBoard(Guid.NewGuid(), OwnerOrganizationUserId,
                    "Evelyn Brooks", null, null, 1, [])]);
            else if (path.EndsWith("/users"))
                result = Array.Empty<object>();
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
