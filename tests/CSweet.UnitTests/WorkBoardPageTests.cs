using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Realtime;
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
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class WorkBoardPageTests
{
    [Theory]
    [InlineData(AppRealtimeEvents.WorkBoardChanged)]
    [InlineData(Wire.WorkstreamEventNames.SprintChangedV1)]
    public async Task EmbeddedWorkRefreshesSprintsOnScopedEventsAndReconnect(string eventType)
    {
        var api = new BoardApi(false, true);
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        services.AddScoped<AppRealtimeState>();
        var activator = new BoardActivator();
        services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<WorkBoards>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkBoards.OrganizationId)] = BoardApi.OrganizationId,
                [nameof(WorkBoards.BoardId)] = BoardApi.BoardId,
                [nameof(WorkBoards.Embedded)] = true
            }));
            Assert.Contains("No active sprint", rendered.ToHtmlString());
            var requests = api.SprintRequests;
            var now = DateTimeOffset.UtcNow;
            var first = new WorkSprintResponse(BoardApi.SprintId, BoardApi.BoardId, "First sprint", "", "Active",
                null, null, null, null, null, 0, 0, 0, 0, 1, now, now);
            api.Sprints = [first];
            Publish(Guid.NewGuid(), BoardApi.BoardId);
            Publish(BoardApi.OrganizationId, Guid.NewGuid());
            Publish(BoardApi.OrganizationId, BoardApi.BoardId, malformed: true);
            Assert.Equal(requests, api.SprintRequests);
            Publish(BoardApi.OrganizationId, BoardApi.BoardId);
            await WaitForHtmlAsync("First sprint · Active");
            Assert.True(api.SprintRequests > requests);
            Assert.DoesNotContain("No active sprint", rendered.ToHtmlString());

            var next = first with { Id = Guid.NewGuid(), Name = "Next sprint" };
            api.Sprints = [first with { Status = "Completed" }, next];
            Publish(BoardApi.OrganizationId, BoardApi.BoardId);
            await WaitForHtmlAsync("title=\"Next sprint · Active\"");
            api.Sprints = [first with { Status = "Completed" }, next with { Status = "Completed" }];
            Publish(BoardApi.OrganizationId, BoardApi.BoardId);
            await WaitForHtmlAsync("No active sprint");

            // A missed notification is recovered by re-reading when the connection returns.
            api.Sprints = [next];
            typeof(WorkBoards).GetMethod("OnRealtimeReconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Board, null);
            await WaitForHtmlAsync("title=\"Next sprint · Active\"");
            await activator.Board!.DisposeAsync();
            requests = api.SprintRequests;
            Publish(BoardApi.OrganizationId, BoardApi.BoardId);
            Assert.Equal(requests, api.SprintRequests);

            async Task WaitForHtmlAsync(string expected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!WebUtility.HtmlDecode(rendered.ToHtmlString()).Contains(expected, StringComparison.Ordinal))
                    await Task.Delay(10, timeout.Token);
            }
        });

        void Publish(Guid organizationId, Guid boardId, bool malformed = false)
        {
            object data = malformed ? new { boardId = 42 } : eventType == AppRealtimeEvents.WorkBoardChanged
                ? new { boardId } : new { context = new { boardId } };
            typeof(AppRealtimeState).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(provider.GetRequiredService<AppRealtimeState>(), [new AppRealtimeEventEnvelope(Guid.NewGuid(), 1,
                    eventType, organizationId, "board", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data))]);
        }
    }

    private sealed class BoardActivator : IComponentActivator
    {
        public WorkBoards? Board { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is WorkBoards board) Board = board;
            return component;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectBoardRedirectsLegacyPageButOpensRequestedTicketWhenEmbedded(bool embedded)
    {
        var api = new BoardApi(false, projectBoard: true);
        var navigation = new TestNavigation();
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkBoards>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkBoards.OrganizationId)] = BoardApi.OrganizationId,
                [nameof(WorkBoards.BoardId)] = BoardApi.BoardId,
                [nameof(WorkBoards.Embedded)] = embedded,
                [nameof(WorkBoards.RequestedItemId)] = BoardApi.ItemId,
                [nameof(WorkBoards.RequestedSprintId)] = BoardApi.SprintId
            }));
            return component.ToHtmlString();
        });
        if (embedded)
        {
            Assert.Null(navigation.Destination);
            Assert.Contains("Deep-linked ticket description", html);
            Assert.Contains("mud-drawer--open", html);
        }
        else
        {
            Assert.Equal($"/organizations/{BoardApi.OrganizationId}/projects/{BoardApi.ProjectId}?tab=work&board={BoardApi.BoardId}&item={BoardApi.ItemId}&sprint={BoardApi.SprintId}", navigation.Destination);
            Assert.True(navigation.ReplaceHistory);
        }
    }

    [Fact]
    public async Task BoardDirectoryShowsPersonalBoardLoadFailure()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
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
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new BoardApi(sprintRequestFails)) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<IAgentApiClient, AgentApiClient>();
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
        services.AddMudServices(); services.AddCSweetLocalization();
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

    private sealed class BoardApi(bool sprintRequestFails, bool projectBoard = false) : HttpMessageHandler
    {
        public IReadOnlyList<WorkSprintResponse> Sprints { get; set; } = [];
        public int SprintRequests { get; private set; }
        public static readonly Guid OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid();
        public static readonly Guid ProjectId = Guid.NewGuid(), ItemId = Guid.NewGuid(), SprintId = Guid.NewGuid();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var summary = new WorkBoardSummaryResponse(BoardId, OrganizationId, projectBoard ? ProjectId : null, "BLAST GRID", "Game delivery", false, false, false,
                null, 0, 0, 1, now, now, [WorkSprintActions.Read]) { Key = "GAME" };
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/boards"))
                result = new WorkBoardDirectoryResponse([summary], true);
            else if (path.EndsWith(BoardId.ToString()))
                result = new WorkBoardDetailResponse(summary, [], projectBoard
                    ? [new(ItemId, BoardId, Guid.NewGuid(), null, null, "Task", "Requested ticket", "Deep-linked ticket description", "Ready", "Medium", null, 1, 1, null, now, now)] : []);
            else if (path.EndsWith("/sprints"))
            {
                SprintRequests++;
                if (sprintRequestFails)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                result = Sprints;
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
        public string? Destination { get; private set; }
        public bool ReplaceHistory { get; private set; }
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void NavigateToCore(string uri, NavigationOptions options) { Destination = uri; ReplaceHistory = options.ReplaceHistoryEntry; }
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
