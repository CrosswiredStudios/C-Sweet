using System.Reflection;
using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Core;
using Wire = CSweet.WorkManagement.Contracts;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.WorkManagement;

namespace CSweet.UnitTests;
public sealed class OverviewIssueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerRecoveryAndItsSourceWaitForAnActionableChange(bool recovery)
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var personalBoard = Guid.NewGuid();
        var task = JsonSerializer.Deserialize<Wire.PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), BoardId = personalBoard,
            OwnerOrganizationUserId = f.Specialist.Id, Title = "Review blocked work", Status = Wire.PersonalTodoStatuses.Blocked,
            BlockReason = "Manager recovery needs assistance: platform correction required", IsExecutable = true,
            CorrelationId = recovery ? CSweet.Agent.SDK.ManagerWorkRecovery.Prefix + "source:1" : "ordinary-work",
            WorkContext = new(WorkstreamId: f.Board.WorkstreamId) };
        var source = task with { Id = Guid.NewGuid(), Title = "Receive planning advice", CorrelationId = null,
            WorkContext = new(WorkstreamId: f.Board.WorkstreamId, CoordinationSessionId: Guid.NewGuid()) };
        task = task with { WorkContext = new(WorkstreamId: f.Board.WorkstreamId, WorkItemId: source.Id) };
        var secondReview = task with { Id = Guid.NewGuid(), WorkContext = new(WorkstreamId: f.Board.WorkstreamId, WorkItemId: task.Id) };
        var todos = DispatchProxy.Create<IPersonalTodoService, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)todos).InvokeMethod = (method, _) => method == "ListAsync"
            ? Task.FromResult(new Wire.PersonalTodoDirectory([new(personalBoard, f.Specialist.Id, "Director", f.Manager.Id, "Owner", 1, [task, secondReview, source])]))
            : throw new NotSupportedException(method);
        var decisions = DispatchProxy.Create<IExecutiveDecisionService, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)decisions).InvokeMethod = (_, _) => Task.FromResult<IReadOnlyList<PendingAgentQuestionResponse>>([]);
        var reader = new OverviewIssueReader(f.Db, todos, new(f.Db, todos, TimeProvider.System, new(f.Db, todos, new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(f.Db), f.Service)), decisions,
            new(f.Db, new(f.Db, TimeProvider.System), TimeProvider.System), f.Service);
        var directory = await reader.ReadAsync(f.Org, new(f.Manager.Id, null), false, default);
        Assert.Empty(directory.UserActions);
        if (recovery)
        {
            var one = Assert.Single(directory.Team);
            Assert.Equal(source.Title, one.Title);
            Assert.Equal(3, one.Work.Count);
            Assert.Equal(3, one.AffectedWork.Count);
            Assert.All(one.Work, w => Assert.False(w.CanRetry));
        }
        else Assert.Equal(3, directory.Team.Count); // Sharing a project, owner or title cannot establish a blocker.
        Assert.All(directory.Items, card => Assert.Equal(f.Specialist.Id, card.OwnerOrganizationUserId));
    }

    private static OverviewIssueCard Card(string key, Guid project, bool action = false) =>
        new(key, Guid.NewGuid(), project, "Brickwave", "A concise title", "One sentence explains the issue.", "Gabriel", "Review current evidence", action, false, [], [], [], "Full retained findings");
    [Fact]
    public void DuplicateSourceAndRewordedPresentationHaveOneCurrentCardButRetainDetail()
    {
        var project = Guid.NewGuid(); var issue = Card("case:stable", project, true);
        var directory = OverviewIssueDirectory.Consolidate([issue, issue with { Title = "Reworded title", Detail = "Additional evidence" }]);
        var current = Assert.Single(directory.UserActions); Assert.Empty(directory.Team);
        Assert.Contains("Full retained findings", current.Detail); Assert.Contains("Additional evidence", current.Detail);
    }
    [Fact]
    public void RetryPermissionDoesNotMakeTeamWorkAnOwnerDecisionAndDifferentDecisionsRemainVisible()
    {
        var project = Guid.NewGuid();
        var directory = OverviewIssueDirectory.Consolidate([Card("consultation", project), Card("tracking", project),
            Card("decision:one", project, true), Card("decision:two", project, true)]);
        Assert.Equal(2, directory.UserActions.Count);
        Assert.Equal(2, directory.Team.Count);
        Assert.All(directory.UserActions, c => Assert.True(c.RequiresUserAction));
    }
    [Fact]
    public async Task LegacyBlockedTrackingAndAnExactPendingDecisionProjectAsOneActionableIssue()
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Board.WorkstreamId,
            InitiatorOrganizationUserId = f.Manager.Id, TargetOrganizationUserId = f.Specialist.Id, Status = AgentCoordinationStatus.Failed };
        var decision = new WorkstreamDecisionRecord { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Board.WorkstreamId!.Value, Status = "Pending" };
        var executive = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Org, OptionsJson = JsonSerializer.Serialize(new { workstreamDecisionId = decision.Id }) };
        var affected = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, Title = "Prepare the playtest" };
        f.Db.AddRange(session, decision, executive, affected); await f.Db.SaveChangesAsync();
        var issue = new Wire.WorkIssueUpdate("legacy-feasibility", "The approved technical feasibility must be reconciled", "Full failed-review details", []) {
            Links = [new("Coordination", session.Id), new("Decision", decision.Id), new("WorkItem", affected.Id)] };
        await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source, [issue], "adopt"), default);
        var tracking = JsonSerializer.Deserialize<Wire.PersonalTodoItem>("{}")! with { Id = Guid.NewGuid(), Status = Wire.PersonalTodoStatuses.Blocked,
            Title = "Get feasibility review", BlockReason = "Old attempt failed", WorkContext = new(WorkstreamId: f.Board.WorkstreamId, CoordinationSessionId: session.Id) };
        var todos = DispatchProxy.Create<IPersonalTodoService, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)todos).InvokeMethod = (method, _) => method == "ListAsync" ?
            Task.FromResult(new Wire.PersonalTodoDirectory([new(Guid.NewGuid(), f.Specialist.Id, "Victor", f.Manager.Id, "Gabriel", 1, [tracking])])) : throw new NotSupportedException(method);
        IReadOnlyList<PendingAgentQuestionResponse> questions = [new(Guid.NewGuid(), f.Manager.Id, "Marcus",
            new(executive.Id, "Choose the current technical direction", "Pending", [], "remediate", null, null, DateTimeOffset.UtcNow, null))];
        var decisions = DispatchProxy.Create<IExecutiveDecisionService, WorkIssuePostgresTests.TestProxy>();
        ((WorkIssuePostgresTests.TestProxy)(object)decisions).InvokeMethod = (method, _) => method == "ListPendingForUserAsync" ? Task.FromResult(questions) : throw new NotSupportedException(method);
        var reader = new OverviewIssueReader(f.Db, todos, new(f.Db, todos, TimeProvider.System, new(f.Db, todos, new CSweet.Infrastructure.Security.ScopedActionAuthorizationService(f.Db), f.Service)), decisions,
            new(f.Db, new(f.Db, TimeProvider.System), TimeProvider.System), f.Service);
        var directory = await reader.ReadAsync(f.Org, new(f.Manager.Id, null), false, default);
        var card = Assert.Single(directory.UserActions); Assert.Single(card.Work); Assert.Contains(card.AffectedWork, w => w.ItemId == affected.Id && w.ActionUri.Contains(f.Board.Id.ToString())); Assert.Single(card.Questions); Assert.Empty(directory.Team);
        Assert.Equal(f.Manager.Id, card.OwnerOrganizationUserId);
        questions = [];
        directory = await reader.ReadAsync(f.Org, new(f.Manager.Id, null), false, default);
        Assert.Empty(directory.UserActions); Assert.Equal(card.Key, Assert.Single(directory.Team).Key); // Agent recovery ownership does not depend on retry permission.
        Assert.Equal(AgentCoordinationStatus.Failed, session.Status); Assert.Equal("Pending", decision.Status); // Projection is read-only.
    }

    [Fact]
    public void HistoryDoesNotReturnToTheActiveQueueOrHideAStillRequiredDecision()
    {
        var project = Guid.NewGuid();
        var directory = OverviewIssueDirectory.Consolidate([Card("historical", project) with { Status = "Resolved" }, Card("pending-decision", project, true)]);
        Assert.Single(directory.UserActions); Assert.Empty(directory.Team); Assert.Equal(2, directory.Items.Count);
    }
}
