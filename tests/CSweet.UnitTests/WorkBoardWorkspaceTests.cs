using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.WorkBoards;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkBoardWorkspaceTests
{
    private static readonly Guid BoardId = Guid.NewGuid(), ColumnId = Guid.NewGuid();

    [Fact]
    public void SelectSprintPrefersActiveThenPausedAndPreservesExplicitPastSelection()
    {
        var active = Sprint("Active"); var paused = Sprint("Paused"); var past = Sprint("Completed"); var planned = Sprint("Planned");
        Assert.Equal(active.Id, WorkBoardWorkspacePresentation.SelectSprint([past, paused, planned, active], null));
        Assert.Equal(paused.Id, WorkBoardWorkspacePresentation.SelectSprint([planned, paused], null));
        Assert.Equal(past.Id, WorkBoardWorkspacePresentation.SelectSprint([active, past], past.Id));
        Assert.Equal(active.Id, WorkBoardWorkspacePresentation.SelectSprint([active], Guid.NewGuid()));
        Assert.Null(WorkBoardWorkspacePresentation.SelectSprint([planned, past], null));
    }

    [Fact]
    public void SprintAndBacklogScopesDoNotMixUnscheduledPlannedAndFinishedWork()
    {
        var sprint = Sprint("Active"); var planned = Sprint("Planned");
        var current = Item("Current", sprint.Id); var future = Item("Future", planned.Id);
        var backlog = Item("Backlog"); var done = Item("Unscheduled done") with { Status = "Completed" };
        var cancelled = Item("Cancelled") with { Status = "Cancelled" };
        WorkBoardItemResponse[] items = [current, future, backlog, done, cancelled];
        Assert.Equal([current], WorkBoardWorkspacePresentation.ScopeItems(items, WorkBoardView.Sprint, sprint.Id));
        Assert.Equal([backlog], WorkBoardWorkspacePresentation.ScopeItems(items, WorkBoardView.Backlog, null));
        Assert.Empty(WorkBoardWorkspacePresentation.ScopeItems(items, WorkBoardView.Sprint, null));
        Assert.Equal(5, WorkBoardWorkspacePresentation.ScopeItems(items, WorkBoardView.All, null).Count);
    }

    [Theory]
    [InlineData("video-game.research-spike.v1", "Research Spike")]
    [InlineData("video-game.milestone.v1", "Milestone")]
    [InlineData("software.task.v1", "Task")]
    [InlineData("", "Task")]
    public void CardTypesAreReadable(string key, string expected) =>
        Assert.Equal(expected, WorkBoardWorkspacePresentation.TypeLabel(Item("Title") with { TypeKey = key }));

    [Fact]
    public void IdentifierOnlyShortensItsOwnBoardPrefixAndSearchFindsOwners()
    {
        var item = Item("Title") with { Identifier = "GAME-123", AssignedDisplayName = "Technical Artist" };
        Assert.Equal("#123", WorkBoardWorkspacePresentation.ShortIdentifier(item, "GAME"));
        Assert.Equal("GAME-123", WorkBoardWorkspacePresentation.ShortIdentifier(item, "OTHER"));
        Assert.True(WorkBoardWorkspacePresentation.Matches(item, " artist "));
    }

    [Fact]
    public void AttentionUsesCurrentApprovalsAndExplicitExecutionFailures()
    {
        var item = Item("Movement") with { PlanningRevision = 2 };
        var approval = new Wire.WorkItemApproval(Guid.NewGuid(), "Review", "ChangesRequested", 1, null, null, "Technical", null, null, "Revise", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Null(WorkBoardWorkspacePresentation.AttentionReason(item with { Approvals = [approval] }, null));
        Assert.Equal("Approval changes requested", WorkBoardWorkspacePresentation.AttentionReason(item with { Approvals = [approval with { PlanningRevision = 2 }] }, null));
        var execution = ExecutionItem(item, "Waiting for input decision");
        Assert.Equal("Waiting for input decision", WorkBoardWorkspacePresentation.AttentionReason(item, execution));
        Assert.Null(WorkBoardWorkspacePresentation.AttentionReason(item with { Status = "Completed" }, execution));
    }

    [Fact]
    public async Task RenderedSprintKeepsFullBoardWipAndHidesUnscheduledCards()
    {
        var sprint = Sprint("Active"); var item = Item("Visible sprint task", sprint.Id);
        var board = Detail([item, Item("Unscheduled task"), Item("Other sprint", Guid.NewGuid())], [WorkSprintActions.Read, WorkItemActions.Move]);
        var (html, requests) = await Render(board, [sprint]);
        Assert.Contains("Visible sprint task", html);
        Assert.DoesNotContain("Unscheduled task", html);
        Assert.DoesNotContain("Other sprint", html);
        Assert.Contains("3 / 12 WIP", html);
        Assert.Contains("2 / 8 complete", html);
        Assert.Contains("value=\"25\"", html);
        Assert.Contains("<section class=\"workflow-column", html);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task NoSprintIsAnExplicitEmptyStateRatherThanTheWholeBacklog()
    {
        var (html, _) = await Render(Detail([Item("Unscheduled task")], [WorkSprintActions.Read]), [Sprint("Planned")]);
        Assert.Contains("No active sprint", html);
        Assert.Contains("View backlog", html);
        Assert.DoesNotContain("Unscheduled task", html);
        Assert.DoesNotContain("workflow-column-header", html);
    }

    [Fact]
    public async Task WithoutSprintPermissionShowAllWorkWithoutSprintOrExecutionRequests()
    {
        var (html, requests) = await Render(Detail([Item("Visible all work")], []), []);
        Assert.Contains("All board work", html);
        Assert.Contains("Visible all work", html);
        Assert.DoesNotContain("Select sprint", html);
        Assert.DoesNotContain("New work item", html);
        Assert.DoesNotContain("ticket-drag", html);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task ArchivedBoardRemainsReadableWithoutCreateOrDrag()
    {
        var board = Detail([Item("Archived work")], [WorkItemActions.Create, WorkItemActions.Move]);
        var (html, _) = await Render(board with { Board = board.Board with { IsArchived = true } }, []);
        Assert.Contains("Archived work", html);
        Assert.Contains("read only", html);
        Assert.DoesNotContain("New work item", html);
        Assert.DoesNotContain("ticket-drag", html);
    }

    [Fact]
    public async Task ExecutionErrorsAreNotPresentedAsAHealthySprint()
    {
        var sprint = Sprint("Active");
        var (html, requests) = await Render(Detail([Item("Visible", sprint.Id)], [WorkSprintActions.Read, WorkOrchestrationActions.Read]), [sprint], HttpStatusCode.ServiceUnavailable);
        Assert.Single(requests);
        Assert.Contains("Attention counts may be incomplete", html);
        Assert.Contains("Visible", html);
    }

    [Fact]
    public async Task ExecutionBlockersAreVisibleAndEncoded()
    {
        var sprint = Sprint("Active"); var item = Item("Movement", sprint.Id);
        var execution = new Wire.WorkSprintExecutionResponse(Guid.NewGuid(), BoardId, sprint.Id, Guid.NewGuid(), Guid.NewGuid(), "Active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, [ExecutionItem(item, "Decision <script> required")]);
        var (html, _) = await Render(Detail([item], [WorkSprintActions.Read, WorkOrchestrationActions.Read]), [sprint], HttpStatusCode.OK, execution);
        Assert.Contains("1 need attention", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    private static WorkSprintResponse Sprint(string status) => new(Guid.NewGuid(), BoardId, $"Sprint {status}", "Deliver the core loop", status,
        null, null, null, null, null, 8, 2, 20, 5, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static WorkBoardItemResponse Item(string title, Guid? sprint = null) => new(Guid.NewGuid(), BoardId, ColumnId, null, sprint, "Task", title,
        "Details", "Active", "High", 3, 1, 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Identifier = "GAME-1" };
    private static WorkBoardDetailResponse Detail(IReadOnlyList<WorkBoardItemResponse> items, IReadOnlyList<string> actions) => new(
        new(BoardId, Guid.NewGuid(), null, "BLAST GRID", "Game delivery", false, false, false, null, items.Count, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, actions) { Key = "GAME" },
        [new(ColumnId, "In progress", "InProgress", 0, "HardLimit", 12)], items);
    private static Wire.WorkItemExecutionResponse ExecutionItem(WorkBoardItemResponse item, string reason) => new(Guid.NewGuid(), item.Id, item.Identifier!, "Build", 1, "Blocked", reason, [], DateTimeOffset.UtcNow);

    private static async Task<(string Html, List<string> Requests)> Render(WorkBoardDetailResponse board, IReadOnlyList<WorkSprintResponse> sprints,
        HttpStatusCode response = HttpStatusCode.NotFound, Wire.WorkSprintExecutionResponse? execution = null)
    {
        var requests = new List<string>();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient(new Handler(requests, response, execution)) { BaseAddress = new Uri("http://localhost") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkBoardWorkspace>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(WorkBoardWorkspace.Detail)] = board, [nameof(WorkBoardWorkspace.Sprints)] = sprints }));
            return component.ToHtmlString();
        });
        return (html, requests);
    }
    private sealed class Handler(List<string> requests, HttpStatusCode status, Wire.WorkSprintExecutionResponse? execution) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(execution) });
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
