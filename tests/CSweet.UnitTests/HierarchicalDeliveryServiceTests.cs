using System.Reflection;
using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class HierarchicalDeliveryServiceTests
{
    [Fact]
    public async Task ReleaseRegressionRemediationCanAmendScopeAfterStoryIntegrationAndRequiresNewTaskQa()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure()); await f.CompleteTasks();
        for (var pass = 0; pass < 8; pass++)
        {
            await f.Service.PulseAsync(); var current = await f.Read();
            var release = current.Executions.FirstOrDefault(x => x.Scope == "Release" && x.Status == "WaitingForHuman");
            if (release is not null)
            {
                var request = f.Review(release);
                await f.Service.CompleteReviewAsync(f.Org, f.Qa.Id, request with { Result = request.Result with
                    { Approved = false, Criteria = [new("Requirement works", false, "Regression found a defect")], Findings = ["Repair release regression"] } });
                break;
            }
            var review = current.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
            if (review is not null) { await f.Approve(review); continue; }
            var acceptance = current.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval");
            if (acceptance is not null) await f.Accept(acceptance);
        }
        var plan = await f.Read(); var finding = Assert.Single(plan.Findings);
        Assert.Contains(plan.Executions, x => x.Scope == "Story" && x.Promotions.Any(p => p.Status == "Completed"));
        plan = await f.Service.ControlAsync(f.Org, f.Manager.Id, new(plan.Id, plan.Revision, "pause", "pause-for-remediation"));
        var remediation = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, ParentWorkTaskId = f.Story.Id,
            IsExecutable = true, Kind = WorkItemKind.Task, Title = "Repair regression", PlanningRevision = 1,
            PlanningSpecificationJson = f.Task.PlanningSpecificationJson, DeliverySpecificationJson = f.Task.DeliverySpecificationJson };
        f.Db.CoreWorkTasks.Add(remediation);
        foreach (var assignment in await f.Db.WorkItemStageAssignments.Where(x => x.WorkItemId == f.Task.Id).ToListAsync())
            f.Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, WorkItemId = remediation.Id,
                StageKey = assignment.StageKey, PrincipalKind = assignment.PrincipalKind, OrganizationUserId = assignment.OrganizationUserId, PlatformAction = assignment.PlatformAction });
        await f.Db.SaveChangesAsync();
        plan = await f.Service.ConfigureAsync(f.Org, f.Manager.Id, f.Request() with
            { PlanId = plan.Id, ExpectedRevision = plan.Revision, IdempotencyKey = "approved-remediation-scope" });
        await f.Service.ControlAsync(f.Org, f.Manager.Id, new(plan.Id, plan.Revision, "activate", "activate-remediation"));
        var original = f.Task; f.Task = remediation; await f.CompleteTasks(); f.Task = original;
        await f.Service.PulseAsync(); var stopped = (await f.Read()).Executions.Last(x => x.Scope == "Story" && x.Status == "Blocked");
        var recover = new RecoverWorkDeliveryRequest(f.PlanId, stopped.Id, stopped.Revision, "resolve-regression", "Reviewed and QA-verified remediation")
            { Resolutions = [new(finding.Id, [remediation.Id], "Fresh task QA addresses the linked finding")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RecoverAsync(f.Org, f.Manager.Id, recover with
            { Resolutions = [new(finding.Id, [original.Id], "Old task QA cannot resolve a new regression")] }));
        plan = await f.Service.RecoverAsync(f.Org, f.Manager.Id, recover);
        Assert.Equal("Resolved", Assert.Single(plan.Findings).Status);
        await f.Service.PulseAsync();
        Assert.Equal("WaitingForHuman", (await f.Read()).Executions.Single(x => x.Id == stopped.Id).Status);
    }
    [Fact]
    public async Task CrossBoardReleaseRequiresBothBoardAndRepositoryAccessAndOneReleaseAcceptance()
    {
        await using var f = await Fixture.Create(false, twoRepositories: true);
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = f.Org, Name = "Second team", NormalizedName = "SECOND", TeamKey = "second" };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project, TeamId = team.Id,
            ManagerOrganizationUserId = f.Manager.Id, Name = "Second board", Key = "SECOND", ProfileKey = WorkBoardProfileKeys.SoftwareDeliveryV2 };
        WorkTask Item(WorkItemKind kind, Guid? parent) => new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id,
            Kind = kind, IsExecutable = kind == WorkItemKind.Task, ParentWorkTaskId = parent, Title = kind.ToString(), PlanningRevision = 1,
            PlanningSpecificationJson = f.Task.PlanningSpecificationJson };
        var epic = Item(WorkItemKind.Epic, null); var story = Item(WorkItemKind.Story, epic.Id); var task = Item(WorkItemKind.Task, story.Id);
        f.Db.AddRange(team, board, epic, story, task);
        foreach (var person in new[] { f.Manager, f.Author, f.Qa, f.Technical })
            f.Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, OrganizationUserId = person.Id, TeamId = team.Id });
        foreach (var assignment in await f.Db.WorkItemStageAssignments.Where(x => x.WorkItemId == f.Task.Id).ToListAsync())
            f.Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id, WorkItemId = task.Id,
                StageKey = assignment.StageKey, PrincipalKind = assignment.PrincipalKind, OrganizationUserId = assignment.OrganizationUserId, PlatformAction = assignment.PlatformAction });
        await f.Db.SaveChangesAsync();
        var initial = f.Request();
        var request = initial with
        {
            EpicItemIds = [f.Epic.Id, epic.Id], IdempotencyKey = "cross-board-plan",
            Branches = [.. initial.Branches.Where(x => x.Scope != "Story" || x.RepositoryId == f.Repositories[0]),
                new(f.Repositories[1], "Story", story.Id, "codex/story/second", "codex/release/initial")],
            Assignments = [.. initial.Assignments, new("Story", story.Id, board.Id, [new("quality", "Human", f.Qa.Id)]),
                new("Epic", epic.Id, board.Id, [new("technical-review", "Human", f.Technical.Id)])]
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ConfigureAsync(f.Org, f.Manager.Id, request));
        foreach (var person in new[] { f.Manager, f.Author, f.Qa, f.Technical })
            foreach (var action in new[] { CSweet.Contracts.WorkManagement.WorkItemActions.Read, CSweet.Contracts.WorkManagement.WorkBoardActions.Configure })
                f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.OrganizationUser,
                    SubjectId = person.Id, ScopeKind = GrantScopeKind.Board, ScopeId = board.Id, Action = action });
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ConfigureAsync(f.Org, f.Manager.Id, request));
        f.Db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = team.Id, RepositoryId = f.Repositories[1] });
        await f.Db.SaveChangesAsync();
        var plan = await f.Service.ConfigureAsync(f.Org, f.Manager.Id, request); f.PlanId = plan.Id;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var binding in new[] { (Item: f.Task, Repo: f.Repositories[0], Branch: "codex/story/one"), (Item: task, Repo: f.Repositories[1], Branch: "codex/story/second") })
            binding.Item.DeliverySpecificationJson = JsonSerializer.Serialize(new WorkItemDeliverySpecification(binding.Repo, ["Deliver requirement"], ["Requirement works"])
                { DeliveryPlanId = plan.Id, BaseBranch = binding.Branch }, json);
        await f.Db.SaveChangesAsync(); await f.Activate(plan);
        await f.CompleteTasks(); var originalTask = f.Task; var originalBoard = f.Board;
        f.Task = task; f.Board = board; await f.CompleteTasks(); f.Task = originalTask; f.Board = originalBoard;
        var releaseAcceptances = 0;
        for (var pass = 0; pass < 20; pass++)
        {
            await f.Service.PulseAsync(); plan = await f.Read(); if (plan.Status == "Completed") break;
            var review = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
            if (review is not null) { await f.Approve(review); continue; }
            var acceptance = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval");
            if (acceptance is not null) { if (acceptance.Scope == "Release") releaseAcceptances++; await f.Accept(acceptance); }
        }
        Assert.Equal("Completed", (await f.Read()).Status); Assert.Equal(1, releaseAcceptances);
        Assert.Equal(2, (await f.Read()).Executions.Single(x => x.Scope == "Release").Promotions.Count(x => x.Status == "Completed"));
    }
    [Fact]
    public async Task ScopeAmendmentCancelsSupersededReviewsAndRevokesDocumentAccess()
    {
        await using var f = await Fixture.Create(true);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        var plan = await f.Read(); var story = plan.Executions.Single(x => x.Scope == "Story");
        await f.Service.ReadEvidenceAsync(f.Org, f.Qa.Id, new(f.PlanId, story.Id));
        Assert.NotEmpty(await f.Db.ScopedActionGrants.Where(x => x.GrantedBySubjectId == story.Id && x.RevokedAt == null).ToListAsync());
        plan = await f.Service.ControlAsync(f.Org, f.Manager.Id, new(plan.Id, plan.Revision, "pause", "pause-amend"));
        var amended = await f.Service.ConfigureAsync(f.Org, f.Manager.Id, f.Request() with
            { PlanId = plan.Id, ExpectedRevision = plan.Revision, IdempotencyKey = "amend-scope" });
        Assert.Equal(plan.ScopeRevision + 1, amended.ScopeRevision);
        Assert.All(amended.Executions, x => Assert.Equal("Superseded", x.Status));
        Assert.All(amended.Executions.SelectMany(x => x.Stages), x => Assert.Equal("Cancelled", x.Status));
        Assert.Empty(await f.Db.ScopedActionGrants.Where(x => x.GrantedBySubjectId == story.Id && x.RevokedAt == null).ToListAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Approve(story));
    }
    [Fact]
    public async Task ActivatedTaskRejectsChangedStaffingUntilScopeIsAmended()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure());
        await WorkDeliveryTaskAuthorization.RequireAsync(f.Db, f.Task, default);
        var qa = await f.Db.WorkItemStageAssignments.SingleAsync(x => x.WorkItemId == f.Task.Id && x.StageKey == "quality");
        qa.OrganizationUserId = f.Technical.Id;
        await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkDeliveryTaskAuthorization.RequireAsync(f.Db, f.Task, default));
        Assert.Contains("staffing changed", error.Message);
    }

    [Fact]
    public async Task ArtifactCandidateProvidesAuthorizedExactContentAndRevokesTemporaryAccessAfterReview()
    {
        await using var f = await Fixture.Create(true);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        var evidence = await f.Service.ReadEvidenceAsync(f.Org, f.Qa.Id, new(f.PlanId, story.Id));
        Assert.Equal("Exact artifact", Assert.Single(evidence.Documents).Content);
        Assert.Single(evidence.Children);
        await f.Approve(story);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadEvidenceAsync(f.Org, f.Qa.Id, new(f.PlanId, story.Id)));
    }

    [Fact]
    public async Task LostDatabasePromotionReceiptRecoversNativeReceiptWithoutMergingTwice()
    {
        await using var f = await Fixture.Create(false);
        await f.Activate(await f.Configure()); await f.CompleteTasks(); await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        await f.Approve(story);
        var candidate = story.Candidate!.Repositories.Single();
        await f.Client.DeliveryBranchAsync(new(f.Org, candidate.RepositoryId, "InternalGit", null, "", "", "promote",
            candidate.SourceBranch, candidate.TargetBranch, $"promote:{story.Id:N}:{candidate.RepositoryId:N}:{story.Candidate.Digest}",
            candidate.SourceCommitSha, candidate.TargetCommitSha, candidate.CandidateCommitSha));
        await f.Service.PulseAsync();
        story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        Assert.Equal("Completed", story.Status);
        Assert.Single(story.Promotions.Where(x => x.Status == "Completed"));
        Assert.Equal(1, f.Host.SuccessfulPromotions[candidate.RepositoryId]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPlanUsesIndependentTaskQaAndAggregateAcceptanceWithoutSprintIdentities(bool artifacts)
    {
        await using var f = await Fixture.Create(artifacts);
        var draft = await f.Configure();
        var active = await f.Activate(draft);
        Assert.Equal(3, active.Executions.Count);
        Assert.Empty(active.Executions.SelectMany(x => x.Stages));
        await f.Service.PulseAsync();
        Assert.All((await f.Read()).Executions, x => Assert.Equal("WaitingForChildren", x.Status));
        await f.CompleteTasks();
        await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        Assert.Equal("WaitingForHuman", story.Status);
        if (artifacts) Assert.Single(story.Candidate!.Documents); else Assert.Single(story.Candidate!.Repositories);
        await f.Approve(story);
        await f.Service.PulseAsync();
        var epic = (await f.Read()).Executions.Single(x => x.Scope == "Epic");
        await f.Approve(epic);
        epic = (await f.Read()).Executions.Single(x => x.Scope == "Epic");
        Assert.Equal("WaitingForApproval", epic.Status);
        await f.Accept(epic);
        await f.Service.PulseAsync();
        var release = (await f.Read()).Executions.Single(x => x.Scope == "Release");
        await f.Approve(release);
        await f.Approve((await f.Read()).Executions.Single(x => x.Scope == "Release"));
        release = (await f.Read()).Executions.Single(x => x.Scope == "Release");
        Assert.Equal("WaitingForApproval", release.Status);
        await f.Accept(release);
        await f.Service.PulseAsync();
        Assert.Equal("Completed", (await f.Read()).Status);
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        Assert.Equal(WorkTaskStatus.Completed, f.Story.Status);
        Assert.Equal(WorkTaskStatus.Completed, f.Epic.Status);
        Assert.NotEmpty(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == WorkDeliveryCapabilities.Changed).ToListAsync());
        if (artifacts) Assert.Equal(0, f.Host.BranchCalls);
    }

    [Fact]
    public async Task ScopeStaffingAndCandidateChangesCannotReuseReviewAndAuthorCannotQa()
    {
        await using var f = await Fixture.Create(false);
        var request = f.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConfigureAsync(f.Org, f.Manager.Id,
            request with { Assignments = request.Assignments.Select(x => x.Scope == "Story" ? x with { Stages = [new("quality", "Human", f.Author.Id)] } : x).ToArray() }));
        f.Db.ChangeTracker.Clear();
        var draft = await f.Configure(); await f.Activate(draft); await f.CompleteTasks(); await f.Service.PulseAsync();
        var story = (await f.Read()).Executions.Single(x => x.Scope == "Story");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteReviewAsync(f.Org, f.Author.Id, f.Review(story)));
        f.Host.ChangeTarget(story.Candidate!.Repositories.Single());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Approve(story));
        await f.Service.PulseAsync();
        Assert.Equal("Blocked", (await f.Read()).Executions.Single(x => x.Scope == "Story").Status);
    }

    [Fact]
    public async Task PartialPromotionPersistsReceiptsAndResumesOnlyUnfinishedRepositories()
    {
        await using var f = await Fixture.Create(false, twoRepositories: true);
        await f.Activate(await f.Configure()); await f.CompleteTasks();
        // Exercise aggregate sequencing with the complete two-repository candidate.
        for (var count = 0; count < 10; count++)
        {
            await f.Service.PulseAsync();
            var plan = await f.Read();
            var review = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
            if (review is not null) { await f.Approve(review); continue; }
            var acceptance = plan.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval");
            if (acceptance is null) continue;
            if (acceptance.Scope == "Release") { f.Host.FailRepository = acceptance.Candidate!.Repositories.Last().RepositoryId; await f.Accept(acceptance); break; }
            await f.Accept(acceptance);
        }
        await f.Service.PulseAsync();
        var stopped = (await f.Read()).Executions.Single(x => x.Scope == "Release");
        Assert.Equal("PartiallyPromoted", stopped.Status);
        Assert.Single(stopped.Promotions.Where(x => x.Status == "Completed"));
        var success = stopped.Promotions.Single(x => x.Status == "Completed").RepositoryId;
        var successfulCalls = f.Host.SuccessfulPromotions[success];
        f.Host.FailRepository = null;
        await f.Service.RecoverAsync(f.Org, f.Manager.Id, new(f.PlanId, stopped.Id, stopped.Revision, "recover", "Provider restored"));
        await f.Service.PulseAsync();
        Assert.Equal("Completed", (await f.Read()).Status);
        Assert.Equal(successfulCalls, f.Host.SuccessfulPromotions[success]);
    }

    [Fact]
    public async Task PauseCancelIdempotencyAndManualDoneCannotBypassAuthority()
    {
        await using var f = await Fixture.Create(true);
        var draft = await f.Configure();
        Assert.Equal(draft.Id, (await f.Configure()).Id);
        await f.Activate(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkDeliveryTaskAuthorization.PreventManualCompletionAsync(f.Db, f.Task, default));
        var active = await f.Read();
        await f.Service.ControlAsync(f.Org, f.Manager.Id, new(active.Id, active.Revision, "pause", "pause"));
        await f.CompleteTasks(); await f.Service.PulseAsync();
        Assert.Empty((await f.Read()).Executions.SelectMany(x => x.Stages));
        var paused = await f.Read();
        await f.Service.ControlAsync(f.Org, f.Manager.Id, new(paused.Id, paused.Revision, "cancel", "cancel"));
        Assert.All((await f.Read()).Executions, x => Assert.Equal("Cancelled", x.Status));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; }
        public Guid Org = Guid.NewGuid(), Project = Guid.NewGuid(), PlanId;
        public OrganizationUser Manager = null!, Author = null!, Qa = null!, Technical = null!;
        public WorkBoard Board = null!;
        public WorkTask Task = null!, Story = null!, Epic = null!;
        public readonly List<Guid> Repositories = [];
        public readonly bool Artifact;
        public ITrustedSourceControlHostClient Client = DispatchProxy.Create<ITrustedSourceControlHostClient, HostProxy>();
        public HostProxy Host => (HostProxy)Client;
        public WorkDeliveryService Service => new(Db, new ScopedActionAuthorizationService(Db), Client, TimeProvider.System);
        static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Fixture(bool artifact, DbContextOptions<CSweetDbContext>? options = null)
        { Artifact = artifact; Db = new(options ?? new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options); }
        public static async Task<Fixture> Create(bool artifact, bool twoRepositories = false)
        { var f = new Fixture(artifact); await f.Seed(twoRepositories); return f; }
        public async Task Seed(bool twoRepositories = false)
        {
            Db.CoreOrganizations.Add(new() { Id = Org, Name = "Fresh hierarchical project" });
            OrganizationUser Person(string name) => new() { Id = Guid.NewGuid(), OrganizationId = Org, DisplayName = name, IsActive = true, EmployeeType = EmployeeType.Human };
            Manager = Person("Manager"); Author = Person("Author"); Qa = Person("Independent QA"); Technical = Person("Technical reviewer");
            Db.CoreOrganizationUsers.AddRange(Manager, Author, Qa, Technical);
            Db.Workstreams.Add(new() { Id = Project, OrganizationId = Org, Name = "Release", Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = Manager.Id });
            var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = Org, Name = "Delivery", NormalizedName = "DELIVERY", TeamKey = "delivery", LeadOrganizationUserId = Manager.Id };
            Db.OrganizationTeams.Add(team);
            Board = new() { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Project, TeamId = team.Id,
                ManagerOrganizationUserId = Manager.Id, Name = "Team", Key = "TEAM", ProfileKey = WorkBoardProfileKeys.SoftwareDeliveryV2 };
            Db.WorkBoards.Add(Board);
            foreach (var person in new[] { Manager, Author, Qa, Technical })
            {
                Db.ProjectParticipants.Add(new() { OrganizationId = Org, WorkstreamId = Project, OrganizationUserId = person.Id, AddedByOrganizationUserId = Manager.Id });
                Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, OrganizationUserId = person.Id, TeamId = team.Id });
                foreach (var action in WorkDeliveryCapabilities.All)
                    Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, SubjectKind = GrantSubjectKind.OrganizationUser,
                        SubjectId = person.Id, ScopeKind = GrantScopeKind.Workstream, ScopeId = Project, Action = action });
                foreach (var action in new[] { CSweet.Contracts.WorkManagement.WorkItemActions.Read, CSweet.Contracts.WorkManagement.WorkBoardActions.Configure })
                    Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, SubjectKind = GrantSubjectKind.OrganizationUser,
                        SubjectId = person.Id, ScopeKind = GrantScopeKind.Board, ScopeId = Board.Id, Action = action });
            }
            WorkTask Item(WorkItemKind kind, bool executable, Guid? parent) => new() { Id = Guid.NewGuid(), OrganizationId = Org,
                BoardId = Board.Id, Kind = kind, IsExecutable = executable, ParentWorkTaskId = parent, Title = kind.ToString(), PlanningRevision = 1,
                PlanningSpecificationJson = JsonSerializer.Serialize(new WorkItemPlanningSpecification(["Deliver requirement"], ["Requirement works"]), Json) };
            Epic = Item(WorkItemKind.Epic, false, null); Story = Item(WorkItemKind.Story, false, Epic.Id); Task = Item(WorkItemKind.Task, true, Story.Id);
            Db.CoreWorkTasks.AddRange(Epic, Story, Task);
            void Assign(string key, OrganizationUser person) => Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org,
                BoardId = Board.Id, WorkItemId = Task.Id, StageKey = key, PrincipalKind = WorkOrchestrationPrincipalKind.Human, OrganizationUserId = person.Id });
            Assign("development", Author); Assign("quality", Qa);
            if (!Artifact)
            {
                Assign("technical-review", Technical);
                Db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, BoardId = Board.Id, WorkItemId = Task.Id,
                    StageKey = "task-integration", PrincipalKind = WorkOrchestrationPrincipalKind.PlatformAction, PlatformAction = HierarchicalWorkflows.TaskIntegrationAction });
                for (var index = 0; index < (twoRepositories ? 2 : 1); index++)
                {
                    var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = Org, Provider = SourceControlProvider.InternalGit, Status = SourceControlConnectionStatus.Connected };
                    var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = Org, Connection = connection, ConnectionId = connection.Id,
                        Name = "Repository " + index, DefaultBranch = "main", Status = SourceControlRepositoryStatus.Ready };
                    Db.SourceControlRepositories.Add(repository); Repositories.Add(repository.Id);
                    Db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, TeamId = team.Id, RepositoryId = repository.Id });
                }
            }
            await Db.SaveChangesAsync();
        }
        public ConfigureWorkDeliveryPlanRequest Request() => new(Project, "Initial release", Manager.Id, [Epic.Id],
            Repositories.SelectMany(id => new[] { new WorkDeliveryBranchBinding(id, "Release", null, "codex/release/initial", "main"),
                new WorkDeliveryBranchBinding(id, "Story", Story.Id, "codex/story/one", "codex/release/initial") }).ToArray(),
            [new("Story", Story.Id, Board.Id, [new("quality", "Human", Qa.Id)]),
             new("Epic", Epic.Id, Board.Id, [new("technical-review", "Human", Technical.Id)]),
             new("Release", null, Board.Id, [new("quality", "Human", Qa.Id), new("technical-review", "Human", Technical.Id)])], "configure");
        public async Task<WorkDeliveryPlanResponse> Configure()
        {
            var result = await Service.ConfigureAsync(Org, Manager.Id, Request()); PlanId = result.Id;
            Task = await Db.CoreWorkTasks.SingleAsync(x => x.Id == Task.Id);
            Task.DeliverySpecificationJson = JsonSerializer.Serialize(new WorkItemDeliverySpecification(Artifact ? Guid.Empty : Repositories[0],
                ["Deliver requirement"], ["Requirement works"]) { BaseBranch = Artifact ? "" : "codex/story/one", DeliveryKind = Artifact ? "Artifact" : "Code", DeliveryPlanId = PlanId }, Json);
            await Db.SaveChangesAsync(); return result;
        }
        public Task<WorkDeliveryPlanResponse> Activate(WorkDeliveryPlanResponse draft) => Service.ControlAsync(Org, Manager.Id, new(draft.Id, draft.Revision, "activate", "activate"));
        public async Task<WorkDeliveryPlanResponse> Read() => (await Service.ReadAsync(Org, Manager.Id, new(Project, PlanId))).Single();
        public async Task CompleteTasks()
        {
            Task.Status = WorkTaskStatus.Completed;
            var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = Task.Id, Status = WorkItemExecutionStatus.Completed, CreatedAt = DateTimeOffset.UtcNow };
            execution.Stages.Add(new() { Id = Guid.NewGuid(), StageKey = "quality", Status = WorkStageExecutionStatus.Completed, LastOutcomeCode = "passed", CompletedAt = DateTimeOffset.UtcNow });
            if (Artifact)
            {
                var artifact = Guid.NewGuid(); var revision = Guid.NewGuid();
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Exact artifact")));
                Db.CoreArtifacts.Add(new() { Id = artifact, OrganizationId = Org, OriginWorkItemId = Task.Id, WorkstreamId = Project,
                    TeamId = Board.TeamId, CreatedByOrganizationUserId = Author.Id, LatestRevisionId = revision, Title = "Delivered artifact" });
                Db.ArtifactRevisions.Add(new() { Id = revision, ArtifactId = artifact, OrganizationId = Org, ContentSha256 = sha,
                    CreatedByOrganizationUserId = Author.Id, Content = "Exact artifact", Number = 1 });
                var author = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "development", Status = WorkStageExecutionStatus.Completed };
                var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), Status = WorkExecutionAttemptStatus.Completed, CreatedAt = DateTimeOffset.UtcNow };
                attempt.ResultJson = JsonSerializer.Serialize(new WorkExecutionOutcomeV1(author.Id, attempt.Id, "completed", "artifact-delivered", "Delivered exact artifact",
                    JsonSerializer.SerializeToElement(new { artifactId = artifact, revisionId = revision, sha256 = sha }), [], []), Json);
                author.Attempts.Add(attempt); execution.Stages.Add(author);
            }
            Db.WorkItemExecutions.Add(execution); await Db.SaveChangesAsync();
        }
        public CompleteWorkDeliveryReviewRequest Review(WorkDeliveryExecutionResponse execution) => new(PlanId, execution.Id, execution.Stages.Last().Id,
            execution.Revision, new(execution.Candidate!.Digest, true, "Verified exact candidate", [new("Requirement works", true, "Observed against exact content")], [])
                { Validations = execution.Candidate.Repositories.Select(x => new WorkDeliveryValidationEvidence(x.RepositoryId, x.CandidateCommitSha, "regression", 0, true, "Passed")).ToArray() }, Guid.NewGuid().ToString("N"));
        public async Task Approve(WorkDeliveryExecutionResponse execution) => await Service.CompleteReviewAsync(Org,
            execution.Stages.Last().StageKey == "quality" ? Qa.Id : Technical.Id, Review(execution));
        public async Task Accept(WorkDeliveryExecutionResponse execution) => await Service.AcceptAsync(Org, Manager.Id,
            new(PlanId, execution.Id, execution.Revision, execution.Candidate!.Digest, true, "Manager accepts tested integration", [new("Requirement works", true, "Reviewed QA and technical evidence")], [], Guid.NewGuid().ToString("N")));
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    public class HostProxy : DispatchProxy
    {
        public int BranchCalls; public Guid? FailRepository;
        public readonly Dictionary<Guid, int> SuccessfulPromotions = [];
        readonly Dictionary<(Guid, string), string> heads = [];
        readonly Dictionary<string, DeliveryBranchResult> receipts = [];
        static string Sha(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        string Head(Guid repo, string branch) => heads.TryGetValue((repo, branch), out var sha) ? sha : heads[(repo, branch)] = Sha(repo + branch);
        public void ChangeTarget(WorkDeliveryRepositoryCandidate entry) => heads[(entry.RepositoryId, entry.TargetBranch)] = Sha("changed");
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(ITrustedSourceControlHostClient.DeliveryBranchAsync)) throw new NotSupportedException(method.Name);
            BranchCalls++; var r = (DeliveryBranchOperation)args![0]!;
            if (r.Operation is "promote" or "receipt" && receipts.TryGetValue(r.IdempotencyKey, out var replay)) return System.Threading.Tasks.Task.FromResult(replay);
            var source = Head(r.RepositoryId, r.SourceBranch); var target = Head(r.RepositoryId, r.TargetBranch);
            DeliveryBranchResult result = new(source, target, r.Operation == "candidate" ? Sha(source + target) : null);
            if (r.Operation == "ensure") heads[(r.RepositoryId, r.SourceBranch)] = target;
            if (r.Operation == "promote")
            {
                result = new(source, target, r.CandidateCommitSha, r.RepositoryId != FailRepository,
                    r.RepositoryId == FailRepository ? "Provider failed; retry unfinished repository" : null);
                if (result.Promoted)
                { heads[(r.RepositoryId, r.TargetBranch)] = r.CandidateCommitSha!; receipts[r.IdempotencyKey] = result;
                  SuccessfulPromotions[r.RepositoryId] = SuccessfulPromotions.GetValueOrDefault(r.RepositoryId) + 1; }
            }
            return System.Threading.Tasks.Task.FromResult(result);
        }
    }
}
