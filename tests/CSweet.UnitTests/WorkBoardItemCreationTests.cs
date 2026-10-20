using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Pages;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkBoardItemCreationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("agent", "AgentInstallation")]
    [InlineData("human", "Human")]
    public void MemberExecutionUsesTheExactChosenPrincipal(string selection, string kind)
    {
        var page = Page(); var id = Guid.NewGuid();
        Invoke(page, "SetStageMember", "development", $"{selection}:{id:D}");
        var assignment = Assert.Single(Assignments(page));
        Assert.Equal("development", assignment.StageKey); Assert.Equal(kind, assignment.PrincipalKind);
        Assert.Equal(selection == "agent" ? id : (Guid?)null, assignment.AgentInstallationId);
        Assert.Equal(selection == "human" ? id : (Guid?)null, assignment.OrganizationUserId);
        Assert.Equal($"{selection}:{id:D}", Invoke(page, "StageMember", "development"));
    }

    [Fact]
    public void SwitchingPrincipalClearsThePreviousChoiceAndClearingDoesNotInventAnAssignee()
    {
        var page = Page(); var agent = Guid.NewGuid(); var human = Guid.NewGuid();
        Invoke(page, "SetStageMember", "development", $"agent:{agent:D}");
        Invoke(page, "SetStageMember", "development", $"human:{human:D}");
        var assignment = Assert.Single(Assignments(page));
        Assert.Null(assignment.AgentInstallationId); Assert.Equal(human, assignment.OrganizationUserId);
        Invoke(page, "SetStageMember", "development", null);
        Assert.Null(Invoke(page, "StageMember", "development"));
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Assignments(page)).InnerException);
    }

    [Theory]
    [InlineData("manager:11111111-1111-1111-1111-111111111111")]
    [InlineData("agent:not-an-id")]
    [InlineData("human:00000000-0000-0000-0000-000000000000")]
    [InlineData("human")]
    public void MalformedMemberSelectionsCannotCreateAnAssignment(string input)
    {
        var page = Page();
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(
            () => Invoke(page, "SetStageMember", "development", input)).InnerException);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Assignments(page)).InnerException);
    }

    [Fact]
    public async Task CreateRequestCarriesTheSelectedParentAndExactMemberThroughTheExistingEndpoint()
    {
        var page = Page(); var parent = Guid.NewGuid(); var agent = Guid.NewGuid();
        Set(page, "_itemParentId", parent); Set(page, "_itemTitle", "Assigned pilot clarification");
        Set(page, "_itemAccountableOwnerId", Guid.NewGuid());
        Invoke(page, "SetStageMember", "development", $"agent:{agent:D}");
        var api = new CaptureApi();
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        typeof(WorkBoards).GetProperty("Http", Private)!.SetValue(page, http);
        await (Task)Invoke(page, "CreateItemAsync")!;
        Assert.NotNull(api.Request); Assert.Equal(parent, api.Request.ParentItemId);
        Assert.Equal(WorkItemTypeKeys.GeneralTaskV1, api.Request.TypeKey);
        Assert.Equal(agent, Assert.Single(api.Request.StageAssignments).AgentInstallationId);
        Assert.EndsWith($"/boards/{BoardId:D}/items", api.Path);
    }

    private static readonly Guid BoardId = Guid.NewGuid();
    private static WorkBoards Page()
    {
        var page = new WorkBoards();
        Set(page, "_itemAccountableOwnerId", Guid.NewGuid());
        var board = JsonSerializer.Deserialize<WorkBoardSummaryResponse>("{}")! with { Id = BoardId, ProfileKey = WorkBoardProfileKeys.GeneralWorkV1 };
        Set(page, "_detail", new WorkBoardDetailResponse(board, [], []));
        var revisionId = Guid.NewGuid();
        var revision = JsonSerializer.Deserialize<WorkOrchestrationPolicyRevision>("{}")! with
        {
            RevisionId = revisionId,
            Stages = [new("development", "Development", "MemberExecution", null, "", "{}", "{}", 300, null, new())]
        };
        Set(page, "_orchestrationPolicy", new WorkOrchestrationPolicyResponse(Guid.NewGuid(), BoardId, revisionId, [revision]));
        return page;
    }
    private static IReadOnlyList<WorkStageAssignment> Assignments(WorkBoards page) =>
        (IReadOnlyList<WorkStageAssignment>)Invoke(page, "BuildItemStageAssignments")!;
    private static object? Invoke(WorkBoards page, string name, params object?[] arguments) =>
        typeof(WorkBoards).GetMethod(name, Private)!.Invoke(page, arguments);
    private static void Set(WorkBoards page, string name, object? value) => typeof(WorkBoards).GetField(name, Private)!.SetValue(page, value);

    private sealed class CaptureApi : HttpMessageHandler
    {
        internal CreateBoardWorkItemRequest? Request;
        internal string? Path;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Request = await request.Content!.ReadFromJsonAsync<CreateBoardWorkItemRequest>(cancellationToken: cancellationToken);
            Path = request.RequestUri!.AbsolutePath;
            // The component must retain server-side validation failures rather than changing grants or retrying.
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("Parent/assignment validation remains platform-owned.") };
        }
    }
}
