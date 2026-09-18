using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class TaskDeliveryTests
{
    [Fact]
    public async Task Submission_is_idempotent_and_does_not_merge_without_approval()
    {
        await using var f = await Fixture.Create();
        var review = await f.Submit();
        Assert.Equal("AwaitingApproval", review.Status);
        Assert.Equal(WorkTaskStatus.WaitingForApproval, f.Task.Status);
        Assert.Equal(WorkBoardColumnCategory.Testing, (await f.Db.WorkBoardColumns.SingleAsync()).Category);
        Assert.Equal(review.Id, (await f.Submit()).Id);
        await f.Merge();
        Assert.Equal(0, f.Host.Calls);
        Assert.Single(await f.Db.TaskDeliveryReviews.ToListAsync());
    }

    [Theory]
    [InlineData("task", 0)]
    [InlineData("story", 1)]
    [InlineData("epic", 1)]
    public async Task Manager_choices_merge_exact_candidate_and_persist_only_requested_scope(string choice, int preferences)
    {
        await using var f = await Fixture.Create(); var review = await f.Submit();
        await f.Decide(review, choice); await f.Merge();
        var saved = await f.Db.TaskDeliveryReviews.SingleAsync();
        Assert.Equal("Merged", saved.Status); Assert.Equal(1, f.Host.Calls);
        Assert.Equal(f.Publication.CommitSha, f.Host.Request!.ExpectedHeadSha);
        Assert.Equal(preferences, await f.Db.TaskMergePreferences.CountAsync());
        if (preferences > 0) Assert.Equal(choice == "story" ? f.Story.Id : f.Root.Id, (await f.Db.TaskMergePreferences.SingleAsync()).ScopeWorkItemId);
        await f.Merge(); Assert.Equal(1, f.Host.Calls);
    }

    [Fact]
    public async Task Review_first_keeps_task_waiting_until_explicit_follow_up_approval()
    {
        await using var f = await Fixture.Create(); var review = await f.Submit();
        await f.Decide(review, "review"); await f.Merge(); Assert.Equal(0, f.Host.Calls);
        var current = await f.Service.ReadAsync(f.Org, f.Agent, f.Task.Id, default);
        Assert.Equal("ManualReview", current.Status);
        await f.NewMessage(); await f.Decide(current, "task"); await f.Merge(); Assert.Equal(1, f.Host.Calls);
    }

    [Fact]
    public async Task Story_override_and_revocation_apply_to_unstarted_tasks_and_survive_new_service()
    {
        await using var f = await Fixture.Create(); var review = await f.Submit();
        await f.Preference(f.Root.Id, "Auto"); await f.Preference(f.Story.Id, "Ask");
        Assert.Equal("Ask", (await f.Service.PreferenceAsync(f.Org, f.Agent, f.Story.Id, default)).EffectiveMode);
        await f.Merge(); Assert.Equal(0, f.Host.Calls);
        await f.Preference(f.Story.Id, "Inherit");
        Assert.Equal("Auto", (await new TaskDeliveryService(f.Db, TimeProvider.System).PreferenceAsync(f.Org, f.Agent, f.Story.Id, default)).EffectiveMode);
        await f.Preference(f.Root.Id, "Ask"); await f.Merge(); Assert.Equal(0, f.Host.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decide(review, "task"));
        await f.Preference(f.Root.Id, "Auto"); await f.Merge(); Assert.Equal(1, f.Host.Calls);
    }

    [Theory]
    [InlineData("foreign-manager")]
    [InlineData("stale-message")]
    [InlineData("stale-revision")]
    public async Task Unauthorized_or_stale_chat_cannot_change_preferences(string failure)
    {
        await using var f = await Fixture.Create(); await f.Submit();
        var request = new ChangeMergePreferenceRequest(f.Root.Id, "Auto", f.Message.Id, failure == "stale-revision" ? 10 : 0, "preference");
        if (failure == "foreign-manager") { f.Message.SenderOrganizationUserId = Guid.NewGuid(); await f.Db.SaveChangesAsync(); }
        if (failure == "stale-message") await f.NewMessage();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.ChangePreferenceAsync(f.Org, f.Agent, request, default));
        Assert.Empty(await f.Db.TaskMergePreferences.ToListAsync()); await f.Merge(); Assert.Equal(0, f.Host.Calls);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("publication")]
    [InlineData("membership")]
    [InlineData("manager")]
    public async Task Current_authority_and_candidate_are_rechecked_before_merge(string changed)
    {
        await using var f = await Fixture.Create(); var review = await f.Submit(); await f.Decide(review, "task");
        if (changed == "head") f.Publication.CommitSha = new string('c', 40);
        if (changed == "publication") f.Publication.Status = SourceControlPublicationStatus.Superseded;
        if (changed == "membership") (await f.Db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        if (changed == "manager") f.Developer.ReportsToOrganizationUserId = null;
        await f.Db.SaveChangesAsync(); await Assert.ThrowsAnyAsync<Exception>(() => f.Merge()); Assert.Equal(0, f.Host.Calls);
    }

    [Fact]
    public async Task Uncertain_merge_retries_same_operation_after_restart_and_conflict_never_marks_done()
    {
        await using var f = await Fixture.Create(); var review = await f.Submit(); await f.Decide(review, "task");
        f.Host.LoseResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Merge());
        Assert.Equal("Merging", (await f.Db.TaskDeliveryReviews.SingleAsync()).Status);
        var key = f.Host.Request!.IdempotencyKey;
        await f.Merge(); Assert.Equal(key, f.Host.Request.IdempotencyKey); Assert.Equal("Merged", f.Task.MergeStatus);
        Assert.NotEqual(WorkTaskStatus.Completed, f.Task.Status); // Coordinator rolls up only after reading confirmed merge.
    }

    [Fact]
    public async Task Conflict_returns_actionable_feedback_and_keeps_task_uncompleted()
    {
        await using var f = await Fixture.Create(); var review = await f.Submit(); await f.Decide(review, "task");
        f.Host.Conflict = true; await f.Merge();
        Assert.Equal("ChangesRequested", (await f.Db.TaskDeliveryReviews.SingleAsync()).Status);
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status); Assert.Contains("conflict", f.Task.BlockReason);
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(), x => x.EventType == TaskDeliveryCapabilities.Changed);
    }

    [Fact]
    public async Task Qa_assignment_blocks_manager_auto_merge_until_exact_revision_passes()
    {
        await using var f = await Fixture.Create(true); var review = await f.Submit();
        Assert.Equal("Testing", review.Status); Assert.Equal(f.Qa, review.QaInstallationId);
        Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == TaskDeliveryCapabilities.ReviewRequested).ToListAsync());
        await f.Preference(f.Root.Id, "Auto"); await f.Merge(); Assert.Equal(0, f.Host.Calls);
        var current = await f.Service.ReadAsync(f.Org, f.Qa!.Value, f.Task.Id, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.QualityAsync(f.Org, f.Qa.Value,
            new(current.Id, new string('f', 40), "Passed", "Checks pass", [new("test", true, 0, null)], "qa"), default));
        await f.Service.QualityAsync(f.Org, f.Qa.Value, new(current.Id, current.CommitSha, "Passed", "Checks pass", [new("test", true, 0, null)], "qa"), default);
        await f.Merge(); Assert.Equal(1, f.Host.Calls);
    }

    [Fact]
    public async Task Failed_qa_returns_task_to_developer_and_does_not_accept_empty_pass()
    {
        await using var f = await Fixture.Create(true); var review = await f.Submit();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.QualityAsync(f.Org, f.Qa!.Value,
            new(review.Id, review.CommitSha, "Passed", "Looks fine", [], "qa"), default));
        await f.Service.QualityAsync(f.Org, f.Qa!.Value, new(review.Id, review.CommitSha, "Failed", "Ball falls through paddle", [new("test", false, 1, "collision assertion")], "qa"), default);
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status); Assert.Contains("paddle", f.Task.BlockReason);
        await f.Merge(); Assert.Equal(0, f.Host.Calls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ComputeBrokerTests.Fixture Base { get; } = new();
        public CSweet.Infrastructure.Persistence.CSweetDbContext Db => Base.Db;
        public Guid Org => Base.Organization; public Guid Agent => Base.Installation; public Guid? Qa;
        public WorkTask Root = null!, Story = null!, Task = null!;
        public SourceControlPublication Publication = null!;
        public OrganizationUser Developer = null!, Manager = null!;
        public ConversationMessage Message = null!;
        public TaskDeliveryService Service => new(Db, TimeProvider.System);
        public ITrustedSourceControlHostClient Client = DispatchProxy.Create<ITrustedSourceControlHostClient, MergeHost>();
        public MergeHost Host => (MergeHost)Client;
        public static async Task<Fixture> Create(bool qa = false)
        {
            var f = new Fixture(); await f.Base.SeedAsync();
            f.Developer = await f.Db.CoreOrganizationUsers.SingleAsync();
            f.Manager = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, DisplayName = "Manager", IsActive = true, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager };
            f.Developer.ReportsToOrganizationUserId = f.Manager.Id;
            f.Db.CoreOrganizationUsers.Add(f.Manager);
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org, OwnerOrganizationUserId = f.Developer.Id, Kind = WorkBoardKind.Personal, TeamId = Guid.NewGuid() };
            f.Db.WorkBoards.Add(board);
            f.Db.OrganizationTeams.Add(new() { Id = board.TeamId.Value, OrganizationId = f.Org, Name = "Project", NormalizedName = "PROJECT", TeamKey = "project" });
            f.Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = board.TeamId.Value, OrganizationUserId = f.Developer.Id });
            f.Root = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id, Title = "Game", Kind = WorkItemKind.Epic, Status = WorkTaskStatus.Running,
                AssignedAgentInstallationId = f.Agent, ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
            f.Story = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id, Title = "Physics", Kind = WorkItemKind.Story, ParentWorkTaskId = f.Root.Id,
                AssignedAgentInstallationId = f.Agent, PlanningSpecificationJson = JsonSerializer.Serialize(new W.WorkItemPlanningSpecification([], ["Collision works"]) { PersonalPlan = new(f.Root.Id, 1, "Story") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            f.Task = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = board.Id, Title = "Fix paddle", Description = "Fix collision", Kind = WorkItemKind.Task, ParentWorkTaskId = f.Story.Id,
                AssignedAgentInstallationId = f.Agent, AssignmentRevision = 1, Status = WorkTaskStatus.Running,
                PlanningSpecificationJson = JsonSerializer.Serialize(new W.WorkItemPlanningSpecification([], ["Collision works"]) { PersonalPlan = new(f.Root.Id, 2, "Implementation") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            f.Db.CoreWorkTasks.AddRange(f.Root, f.Story, f.Task);
            var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = f.Org, Provider = SourceControlProvider.InternalGit, Status = SourceControlConnectionStatus.Connected };
            var repo = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = f.Org, ConnectionId = connection.Id, Connection = connection, Name = "Game", DefaultBranch = "main", Status = SourceControlRepositoryStatus.Ready, IsPrivate = true };
            f.Db.SourceControlRepositories.Add(repo);
            f.Db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = board.TeamId.Value, RepositoryId = repo.Id });
            var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = board.TeamId.Value, RepositoryId = repo.Id, WorkItemId = f.Task.Id, AssignmentRevision = 1, AgentInstallationId = f.Agent };
            f.Db.SourceControlWorkspaces.Add(workspace);
            f.Publication = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkspaceId = workspace.Id, RepositoryId = repo.Id, CommitSha = new string('a', 40), TicketBranch = "task/fix", TargetBranch = "main", Status = SourceControlPublicationStatus.Published };
            f.Db.SourceControlPublications.Add(f.Publication);
            var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Org };
            f.Db.CoreConversations.Add(chat);
            f.Db.ConversationParticipants.Add(new() { Id = Guid.NewGuid(), ConversationId = chat.Id, OrganizationUserId = f.Developer.Id });
            f.Message = new() { Id = Guid.NewGuid(), ConversationId = chat.Id, Conversation = chat, SenderOrganizationUserId = f.Manager.Id, Sequence = 1, Content = "Approve the task" };
            f.Db.CoreConversationMessages.Add(f.Message);
            if (qa)
            {
                f.Qa = Guid.NewGuid(); var user = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, IsActive = true, EmployeeType = EmployeeType.Agent, AgentInstallationId = f.Qa };
                f.Db.CoreOrganizationUsers.Add(user);
                f.Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = board.TeamId.Value, OrganizationUserId = user.Id });
                var installed = await f.Db.AgentInstallations.SingleAsync();
                f.Db.AgentInstallations.Add(new() { Id = f.Qa.Value, InstallationKey = Guid.NewGuid(), BusinessId = f.Org.ToString("D"), IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active, PackageVersionId = installed.PackageVersionId,
                    Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = f.Qa.Value, ProvidedCapabilitiesJson = "[\"software-quality.validate.v1\"]", RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { TaskDeliveryCapabilities.Quality }) } });
            }
            await f.Db.SaveChangesAsync(); return f;
        }
        public Task<TaskReviewResult> Submit() => Service.SubmitAsync(Org, Agent, new(Root.Id, Task.Id, Publication.Id, "Tested collision", "submit"), default);
        public Task<TaskReviewResult> Decide(TaskReviewResult review, string choice) => Service.DecideFromChatAsync(Org, Agent, new(review.Id, review.Revision, Message.Id, choice, "decide"), default);
        public async Task NewMessage()
        {
            Message = new() { Id = Guid.NewGuid(), ConversationId = Message.ConversationId, Conversation = Message.Conversation, SenderOrganizationUserId = Manager.Id, Sequence = Message.Sequence + 1, Content = "Change merge preference" };
            Db.CoreConversationMessages.Add(Message); await Db.SaveChangesAsync();
        }
        public async Task Preference(Guid scope, string mode)
        {
            await NewMessage(); var value = await Service.PreferenceAsync(Org, Agent, scope, default);
            await Service.ChangePreferenceAsync(Org, Agent, new(scope, mode, Message.Id, value.Revision, Message.Id.ToString()), default);
        }
        public async Task Merge() => await Service.AdvanceMergeAsync(Org, (await Db.TaskDeliveryReviews.SingleAsync()).Id, Client, default);
        public ValueTask DisposeAsync() => Base.DisposeAsync();
    }
    public class MergeHost : DispatchProxy
    {
        public int Calls; public bool LoseResponse, Conflict; public InternalGitMergeRequest? Request;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(ITrustedSourceControlHostClient.MergeInternalAsync)) throw new NotSupportedException(method.Name);
            Request = (InternalGitMergeRequest)args![0]!; Calls++;
            if (LoseResponse) { LoseResponse = false; throw new HttpRequestException("Response lost after merge"); }
            return System.Threading.Tasks.Task.FromResult(Conflict ? new InternalGitMergeResult(false, true, null, "conflict", "Resolve the merge conflict and retest") : new InternalGitMergeResult(true, true, new string('b', 40)));
        }
    }
}
