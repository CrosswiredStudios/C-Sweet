using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class CurrentActivityTests
{
    [Theory]
    [InlineData("chief-review")]
    [InlineData("chief-chat")]
    [InlineData("personal")]
    [InlineData("assigned")]
    [InlineData("discussion")]
    public async Task QueuedTicketsExposeTheirOwnDetailsBeforeTheFirstAttempt(string source)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var work = await f.QueueTicketAsync(source);
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal(f.Ticket.Id, row.WorkItemId);
        Assert.Equal(f.Ticket.BoardId, row.BoardId);
        Assert.Equal(f.Ticket.Title, row.Title);
        Assert.Equal(f.Ticket.Description, row.Description);
        Assert.Equal("Waiting", row.State);
        Assert.Null(row.AttemptId);
        Assert.Null(row.ModelRunId);
        Assert.Equal(work.Id, row.AgentWorkItemId);
        Assert.Equal(source is "assigned" or "discussion" ? null : (Guid?)f.Agent.Id, row.PersonalBoardOwnerId);
        Assert.Empty(await f.Db.WorkExecutionContexts.ToListAsync()); // Reads create no execution authority.
        f.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.False(row.CanInspect);
        Assert.Equal(f.Ticket.Description, row.Description); // The board grant controls ticket details.
        f.Grants.Allow = false;
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }

    [Theory]
    [InlineData("foreign-event")]
    [InlineData("wrong-target")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-board")]
    [InlineData("reassigned-ticket")]
    [InlineData("invalid-json")]
    [InlineData("archived-ticket")]
    [InlineData("archived-board")]
    public async Task QueuedPersonalWakeCannotExposeAnInvalidTicketAssociation(string invalid)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.QueueTicketAsync("personal");
        var source = await f.Db.AgentPlatformEventOutbox.SingleAsync();
        switch (invalid)
        {
            case "foreign-event": source.OrganizationId = Guid.NewGuid(); break;
            case "wrong-target": source.TargetInstallationId = Guid.NewGuid(); break;
            case "wrong-owner": source.DataJson = JsonSerializer.Serialize(new Wire.PersonalTodoAvailableEvent(Guid.NewGuid(), f.Ticket.BoardId!.Value, f.Ticket.Id)); break;
            case "wrong-board": source.DataJson = JsonSerializer.Serialize(new Wire.PersonalTodoAvailableEvent(f.Agent.Id, Guid.NewGuid(), f.Ticket.Id)); break;
            case "reassigned-ticket": f.Ticket.AssignedAgentInstallationId = Guid.NewGuid(); break;
            case "invalid-json": source.DataJson = "{"; break;
            case "archived-ticket": f.Ticket.ArchivedAt = DateTimeOffset.UtcNow; break;
            case "archived-board": (await f.Db.WorkBoards.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
        }
        await f.Db.SaveChangesAsync();
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }

    [Fact]
    public async Task QueuedAssignmentRejectsAnOutdatedAssignmentRevision()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.QueueTicketAsync("assigned");
        f.Ticket.AssignmentRevision++;
        await f.Db.SaveChangesAsync();
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }

    [Fact]
    public async Task QueuedDiscussionUsesItsRecipientRatherThanTheTicketAssignee()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.QueueTicketAsync("discussion");
        f.Ticket.AssignedEmployeeId = Guid.NewGuid(); f.Ticket.AssignedAgentInstallationId = Guid.NewGuid();
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal(f.Agent.Id, row.EmployeeId); Assert.Equal(f.Ticket.Id, row.WorkItemId);
        (await f.Db.AgentPlatformEventOutbox.SingleAsync()).DataJson = JsonSerializer.Serialize(new
        { boardId = f.Ticket.BoardId, itemId = f.Ticket.Id, recipientEmployeeId = Guid.NewGuid() });
        await f.Db.SaveChangesAsync();
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }

    [Fact]
    public async Task SourceTicketFeedRechecksBoardAccessAndRejectsAnOmittedTicket()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var work = await f.QueueTicketAsync("chief-review");
        work.Status = AgentWorkStatus.Leased; work.AttemptCount = 1;
        f.Db.AgentWorkAttempts.Add(f.Attempt); await f.Db.SaveChangesAsync();
        await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
        f.Grants.Allow = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
    }

    [Fact]
    public async Task TicketDescriptionIsRedactedBoundedAndIndependentOfExecutionProgress()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Ticket.Description = "password=secret123 " + new string('x', 1000);
        await f.Db.SaveChangesAsync();
        await f.ProgressAsync(f.Ticket.Id, "Running validation");
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.DoesNotContain("secret123", row.Description);
        Assert.Equal(501, row.Description!.Length);
        Assert.EndsWith("…", row.Description);
        Assert.Equal("Running validation", row.CurrentAction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshModelLifecycleKeepsAnOlderCallbackActiveWithoutStreamChunks(bool completed)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Attempt.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var runId = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var run = await f.Db.AgentRunLogs.SingleAsync(x => x.Id == runId);
        run.StartedAt = DateTimeOffset.UtcNow.AddMinutes(completed ? -10 : -1);
        if (completed) { run.CompletedAt = DateTimeOffset.UtcNow; run.Status = "Completed"; }
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal("Executing", row.State);
        Assert.True(row.LastProgressAt >= DateTimeOffset.UtcNow.AddMinutes(-2));
        f.Attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Db.SaveChangesAsync();
        Assert.Equal("Recovering", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
    }

    [Fact]
    public async Task PendingStreamReceiptsConfirmOnlyTheirExactAttemptAndTaskBeforeLedgerDelivery()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Attempt.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var runId = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        (await f.Db.AgentRunLogs.SingleAsync(x => x.Id == runId)).StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await f.Db.SaveChangesAsync();
        // Remove initial lifecycle receipts to isolate delayed streaming evidence.
        f.Db.AuditOutbox.RemoveRange(await f.Db.AuditOutbox.ToListAsync()); await f.Db.SaveChangesAsync();
        Assert.Equal("Unconfirmed", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
        var unrelated = await f.RunAsync(Guid.NewGuid(), f.Ticket.Id);
        f.Db.QueueAudit(new("model.response.chunk", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: unrelated));
        await f.Db.SaveChangesAsync();
        Assert.Equal("Unconfirmed", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
        f.Db.QueueAudit(new("model.response.chunk", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: runId));
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal("Executing", row.State);
        Assert.True(row.LastProgressAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Empty((await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default)).Entries);
        f.Grants.Allow = false;
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }

    [Theory]
    [InlineData("activity.started")]
    [InlineData("activity.completed")]
    public async Task SdkActivityDeltasAppearInCurrentActionAndAttemptFeed(string kind)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.ProgressAsync(f.Ticket.Id, "LLM provider is processing the request", kind, useDelta: true);
        Assert.Equal("LLM provider is processing the request", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).CurrentAction);
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.Equal("LLM provider is processing the request", Assert.Single(page.Entries).Text);
    }

    [Theory]
    [InlineData(false, "Waiting")]
    [InlineData(true, "Recovering")]
    public async Task PersonalFollowUpDoesNotClaimAnExpiredExecutionLease(bool claimed, string expectedState)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        (await f.Db.WorkBoards.SingleAsync()).Kind = WorkBoardKind.Personal;
        var work = await f.Db.AgentWorkItems.SingleAsync();
        work.Status = AgentWorkStatus.Completed;
        f.Attempt.FinishedAt = DateTimeOffset.UtcNow;
        f.Ticket.ClaimEventId = claimed ? Guid.NewGuid() : null;
        f.Ticket.ClaimExpiresAt = claimed ? DateTimeOffset.UtcNow.AddMinutes(-1) : null;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal(expectedState, row.State);
        Assert.Equal(claimed ? "Execution lease expired; awaiting recovery" : "Waiting for follow-up", row.CurrentAction);
    }

    [Fact]
    public async Task Workspace_progress_survives_content_free_audits_and_newer_model_chatter()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var progressId = await f.ProgressAsync(f.Ticket.Id, "Typecheck failed (exit 2)");
        for (var i = 0; i < 25; i++) await f.ProgressAsync(f.Ticket.Id, null);
        await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal("Typecheck failed (exit 2)", row.CurrentAction);
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.Contains(page.Entries, x => x.Kind == "Workspace" && x.Text == "Typecheck failed (exit 2)");
        f.Db.AgentWorkProgress.Remove(await f.Db.AgentWorkProgress.SingleAsync(x => x.Id == progressId));
        await f.Db.SaveChangesAsync();
        page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.DoesNotContain(page.Entries, x => x.Kind == "Workspace");
    }

    [Fact]
    public async Task Workspace_progress_does_not_cross_task_or_diagnostic_authority()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.ProgressAsync(Guid.NewGuid(), "Other task command");
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.DoesNotContain("Other task", row.CurrentAction);
        Assert.Empty((await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default)).Entries);
        await f.ProgressAsync(f.Ticket.Id, "Private workspace operation");
        f.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.DoesNotContain("Private workspace", row.CurrentAction);
        Assert.Empty(row.PreviousSteps);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
    }

    [Fact]
    public async Task PersonalTaskNavigationUsesBoardOwnerRatherThanExecutor()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var board = await f.Db.WorkBoards.SingleAsync();
        board.Kind = WorkBoardKind.Personal;
        board.OwnerOrganizationUserId = f.Owner.Id;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal(f.Owner.Id, row.PersonalBoardOwnerId);
        Assert.Equal(f.Agent.Id, row.EmployeeId);
        f.Grants.Allow = false;
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }

    [Theory]
    [InlineData(true, WorkTaskStatus.Running)]
    [InlineData(false, WorkTaskStatus.Running)]
    [InlineData(true, WorkTaskStatus.WaitingForApproval)]
    [InlineData(true, WorkTaskStatus.Blocked)]
    public async Task CollaborationUsesExactCurrentDeliveryAndAuthorizedSourceTask(bool canInspect, WorkTaskStatus sourceState)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var work = await f.Db.AgentWorkItems.SingleAsync();
        work.SourceType = "agent-coordination";
        work.SourceId = Guid.NewGuid().ToString(); // No claim-event attribution.
        work.Name = "com.csweet.agent.coordination.turn-requested.v1";
        f.Ticket.Status = sourceState;
        if (sourceState != WorkTaskStatus.Running) f.Ticket.WaitingReason = "Awaiting a project decision";
        foreach (var context in await f.Db.WorkExecutionContexts.ToListAsync()) { context.RootWorkItemId = null; context.WorkItemId = null; }
        var role = new Role { Id = Guid.NewGuid(), OrganizationId = f.Org, Name = "Architect" };
        var partner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent,
            AgentInstallationId = Guid.NewGuid(), DisplayName = "Collaborating architect", RoleId = role.Id };
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
            InitiatorOrganizationUserId = partner.Id, InitiatorInstallationId = partner.AgentInstallationId.Value,
            TargetOrganizationUserId = f.Agent.Id, TargetInstallationId = f.Agent.AgentInstallationId!.Value,
            CurrentOrganizationUserId = f.Agent.Id, CurrentAgentWorkItemId = work.Id, SourceWorkItemId = f.Ticket.Id,
            Subject = "First playable planning" };
        f.Db.AddRange(role, partner, session);
        if (!canInspect) f.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal("Collaboration", row.ActivityKind);
        Assert.Equal(f.Ticket.Id, row.WorkItemId);
        Assert.Equal(f.Ticket.Title, row.Title);
        Assert.Equal((await f.Db.WorkBoards.SingleAsync()).WorkstreamId, row.ProjectId);
        Assert.Equal(canInspect ? session.Subject : null, row.CollaborationName);
        Assert.Equal("Executing", row.State);
        Assert.DoesNotContain("Awaiting a project decision", row.CurrentAction);
        if (canInspect)
        {
            Assert.Equal(partner.Id, row.Collaborator!.EmployeeId);
            Assert.Equal("Architect", row.Collaborator.Role);
            Assert.Equal(work.Name, row.TechnicalName);
            // Session task attribution also binds feed authorization; omitting it cannot bypass a board grant.
            await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
        }
        else { Assert.Null(row.Collaborator); Assert.Null(row.TechnicalName); }
        f.Grants.Allow = false;
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
        if (canInspect)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
    }

    [Fact]
    public async Task StaleOrForeignCollaborationCannotAttachPartnerOrTask()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var work = await f.Db.AgentWorkItems.SingleAsync();
        work.SourceType = "agent-coordination"; work.SourceId = Guid.NewGuid().ToString();
        foreach (var context in await f.Db.WorkExecutionContexts.ToListAsync()) { context.RootWorkItemId = null; context.WorkItemId = null; }
        f.Db.AddRange(
            new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
                CurrentAgentWorkItemId = Guid.NewGuid(), CurrentOrganizationUserId = f.Agent.Id, SourceWorkItemId = f.Ticket.Id },
            new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(),
                CurrentAgentWorkItemId = work.Id, CurrentOrganizationUserId = f.Agent.Id, SourceWorkItemId = f.Ticket.Id });
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.AgentWorkItemId == work.Id);
        Assert.Null(row.Collaborator); Assert.Null(row.WorkItemId);
        Assert.Equal("Collaborating", row.Title);
    }

    [Theory]
    [InlineData("com.csweet.agent.attention.review-due.v1", "Review", "Reviewing work")]
    [InlineData("com.csweet.project-health.review-due.v1", "Review", "Reviewing project health")]
    [InlineData("com.csweet.work.item.discussion.changed.v1", "Discussion", "Responding to a task discussion")]
    [InlineData("work.execution.build-readiness.v1", "Build", "Checking build readiness")]
    [InlineData("work.execution.compute-test.v1", "Testing", "Testing delivery")]
    [InlineData("work.execution.run", "Work", "Executing assigned work")]
    [InlineData("com.example.unknown.event.v2", "Background", "Background activity")]
    public async Task BackgroundEventsHaveHumanReadableTitles(string name, string kind, string title)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Agent.AgentInstallationId!.Value, Name = name, Status = AgentWorkStatus.Pending };
        f.Db.Add(work); await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.AgentWorkItemId == work.Id);
        Assert.Equal(title, row.Title); Assert.Equal(kind, row.ActivityKind); Assert.Equal(name, row.TechnicalName);
    }

    [Theory]
    [InlineData("technical-review", "Review", "Technical review")]
    [InlineData("manager-review", "Review", "Delivery acceptance review")]
    [InlineData("build-readiness", "Build", "Checking build readiness")]
    [InlineData("quality", "Testing", "Testing delivery")]
    public async Task ReleaseStageShowsCanonicalActivityAndScopeWithoutInventingATask(string stageKey, string kind, string label)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var delivery = await f.DeliveryAsync(stageKey);
        await f.RunAsync(f.Attempt.Id, null);
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User, projectsOnly: true)).Items, x => x.AgentWorkItemId == f.Attempt.AgentWorkItemId);
        Assert.Equal(kind, row.ActivityKind); Assert.Equal(label, row.ActivityLabel);
        Assert.Equal("Release: First playable", row.Title);
        Assert.Equal(delivery.Plan!.WorkstreamId, row.ProjectId);
        Assert.Equal(delivery.BoardId, row.BoardId);
        Assert.Null(row.WorkItemId); Assert.Equal("Executing", row.State);
        Assert.Equal("Waiting for model response", row.CurrentAction);
        await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default);
        f.Grants.Allow = false;
        Assert.DoesNotContain((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.AgentWorkItemId == f.Attempt.AgentWorkItemId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
    }

    [Fact]
    public async Task EpicReviewKeepsTaskAuthorizationAndDoesNotInheritItsDependencyWait()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.DeliveryAsync("technical-review", f.Ticket.Id);
        f.Ticket.Status = WorkTaskStatus.WaitingForApproval;
        f.Ticket.WaitingReason = "Waiting for this technical review";
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal(f.Ticket.Id, row.WorkItemId); Assert.Equal("Executing", row.State);
        Assert.Equal("Technical review", row.ActivityLabel);
        await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
    }

    [Theory]
    [InlineData("foreign-organization")]
    [InlineData("wrong-agent")]
    [InlineData("wrong-source")]
    [InlineData("missing-attempt")]
    [InlineData("archived-board")]
    public async Task InvalidDeliveryBindingCannotFallBackToBackgroundOrExposeItsFeed(string invalid)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var delivery = await f.DeliveryAsync("technical-review");
        var stage = Assert.Single(delivery.Stages);
        var work = await f.Db.AgentWorkItems.SingleAsync();
        switch (invalid)
        {
            case "foreign-organization": delivery.Plan!.OrganizationId = Guid.NewGuid(); break;
            case "wrong-agent": stage.AgentInstallationId = Guid.NewGuid(); break;
            case "wrong-source": work.SourceId = Guid.NewGuid().ToString(); break;
            case "missing-attempt": f.Db.WorkExecutionAttempts.RemoveRange(stage.Attempts); break;
            case "archived-board": (await f.Db.WorkBoards.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
        }
        await f.Db.SaveChangesAsync();
        Assert.DoesNotContain((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.AgentWorkItemId == work.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
    }

    [Fact]
    public async Task ReportedActivityTakesPriorityOverNewerModelStatus()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.ProgressAsync(f.Ticket.Id, "Reviewing acceptance criteria", "activity");
        await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal("Reviewing acceptance criteria", row.CurrentAction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queue_explains_provider_blocker_only_when_audit_access_allows_it(bool canInspect)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var queuedId = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var queued = await f.Db.AgentRunLogs.SingleAsync(x => x.Id == queuedId);
        queued.Status = "Queued"; queued.AgentWorkItemId = f.Attempt.AgentWorkItemId;
        var blocker = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent,
            AgentInstallationId = Guid.NewGuid(), DisplayName = "Producer" };
        f.Db.Add(blocker); f.Db.Add(new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentInstallationId = blocker.AgentInstallationId, EmployeeId = blocker.Id, StartedAt = DateTimeOffset.UtcNow,
            Status = "Running", ProviderProfileId = queued.ProviderProfileId, AgentWorkItemId = Guid.NewGuid() });
        if (!canInspect) f.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.WorkItemId == f.Ticket.Id);
        Assert.Equal("Waiting", row.State);
        if (canInspect) Assert.Contains("Producer is generating a response", row.CurrentAction);
        else Assert.DoesNotContain("Producer", row.CurrentAction);
    }

    [Fact]
    public async Task Pending_inbox_work_explains_busy_employee()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var pending = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Agent.AgentInstallationId!.Value, Status = AgentWorkStatus.Pending, Name = "Planning" };
        f.Db.Add(pending); await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.AgentWorkItemId == pending.Id);
        Assert.Equal("Queued while this employee handles another request", row.CurrentAction);
    }

    [Fact]
    public async Task CurrentExecutorWinsOverDirectAssignmentAndRowsAreNotDuplicated()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Ticket.AssignedEmployeeId = Guid.NewGuid();
        await f.Db.SaveChangesAsync();
        var page = await f.Service.ReadAsync(f.Org, f.User);
        var row = Assert.Single(page.Items);
        Assert.Equal(f.Agent.Id, row.EmployeeId);
        Assert.Equal(f.Attempt.Id, row.AttemptId);
        Assert.Equal(f.Ticket.Id, row.WorkItemId);
        Assert.Equal("Executing", row.State);
        Assert.True(row.CanInspect);
    }

    [Fact]
    public async Task BoardAndDiagnosticPermissionsAreIndependentAndRechecked()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Grants.Allow = false;
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
        f.Grants.Allow = true;
        f.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.False(row.CanInspect); Assert.Empty(row.PreviousSteps); Assert.Null(row.Provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(Guid.NewGuid(), f.User));
    }

    [Fact]
    public async Task FeedUsesExactAttemptAndTaskNeverLatestCallForEmployee()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var ours = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var otherTask = await f.RunAsync(f.Attempt.Id, Guid.NewGuid());
        var previousAttempt = await f.RunAsync(Guid.NewGuid(), f.Ticket.Id);
        await f.ChunkAsync(ours, 0, "Visible reasoning");
        await f.ChunkAsync(otherTask, 0, "Different task must not leak");
        await f.ChunkAsync(previousAttempt, 0, "Earlier attempt must not leak");
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.Equal("Visible reasoning", Assert.Single(page.Entries).Text);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, Guid.NewGuid(), 0, default));
        var empty = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, page.NextSequence, default);
        Assert.Empty(empty.Entries);
        await f.ChunkAsync(ours, 1, "Next fragment");
        var next = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, page.NextSequence, default);
        Assert.Equal("Next fragment", Assert.Single(next.Entries).Text);
        Assert.Equal(page.Entries[0].Key, next.Entries[0].Key);
        Assert.Equal(1, next.Entries[0].StreamSequence);
    }

    [Fact]
    public async Task FeedRedactsSecretsAndDoesNotExposeProtectedReasoningOrDuplicateText()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        await f.Writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: run,
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new
            {
                sequence = 0, text = "Output once", contents = new object[]
                {
                    new { kind = "text", text = "Output once" },
                    new { kind = "reasoning", text = "Check validation; password=secret123", protectedData = "private-thought" },
                    new { kind = "function_call", name = "validate", callId = "call-1" },
                    new { kind = "function_result", callId = "call-1", result = new { apiKey = "abc-private", success = true } }
                }
            })));
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("secret123", json); Assert.DoesNotContain("private-thought", json); Assert.DoesNotContain("abc-private", json);
        Assert.Equal("Output once", Assert.Single(page.Entries, x => x.Kind == "Output").Text);
        Assert.Contains(page.Entries, x => x.Label == "Tool requested");
        Assert.Contains(page.Entries, x => x.Label == "Tool result");
    }

    [Fact]
    public async Task InitialFeedIsBoundedAndLateRecordedChunksKeepTheirSourceSequence()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        for (var i = 0; i < 152; i++) await f.ChunkAsync(run, i, $"fragment {i}");
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.Equal(150, page.Entries.Count); Assert.True(page.EarlierEntriesOmitted); Assert.False(page.HasMore);
        await f.ChunkAsync(run, 1, "Late fragment");
        var next = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, page.NextSequence, default);
        Assert.Equal(1, Assert.Single(next.Entries).StreamSequence);
    }

    [Fact]
    public async Task LeaseExpiryAndWaitingAreNotReportedAsExecuting()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Db.SaveChangesAsync();
        Assert.Equal("Recovering", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
        f.Ticket.Status = WorkTaskStatus.WaitingForApproval;
        await f.Db.SaveChangesAsync();
        Assert.Equal("Waiting", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
    }

    [Fact]
    public async Task BackgroundWorkIsDiscoverableWithoutPretendingItBelongsToAProject()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Db.AgentWorkItems.Add(new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Agent.AgentInstallationId!.Value, Name = "Background review", Status = AgentWorkStatus.Pending });
        await f.Db.SaveChangesAsync();
        var page = await f.Service.ReadAsync(f.Org, f.User);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, x => x.Category == "Background" && x.State == "Waiting");
        Assert.Single((await f.Service.ReadAsync(f.Org, f.User, projectsOnly: true)).Items);
    }

    [Fact]
    public async Task ModelCallsOutsideInboxHaveTheirOwnAuthorizedFeed()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org, AgentInstallationId = f.Agent.AgentInstallationId,
            EmployeeId = f.Agent.Id, StartedAt = DateTimeOffset.UtcNow, Status = "Running", Model = "background-model" };
        f.Db.Add(run); await f.Db.SaveChangesAsync();
        await f.ChunkAsync(run.Id, 0, "Background explanation");
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.ModelRunId == run.Id);
        Assert.Equal("Background", row.Category); Assert.Null(row.WorkItemId);
        var page = await f.Service.ModelFeedAsync(f.Org, f.User, f.Agent.Id, run.Id, 0, default);
        Assert.Equal("Background explanation", Assert.Single(page.Entries).Text);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ModelFeedAsync(Guid.NewGuid(), f.User, f.Agent.Id, run.Id, 0, default));
    }

    [Fact]
    public async Task WorkPaginationDoesNotDropOtherEmployeesOrProjects()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        for (var i = 0; i < 55; i++) f.Db.AgentWorkItems.Add(new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Agent.AgentInstallationId!.Value, Status = AgentWorkStatus.Pending, Name = $"Background {i}", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
        await f.Db.SaveChangesAsync();
        var first = await f.Service.ReadAsync(f.Org, f.User);
        var second = await f.Service.ReadAsync(f.Org, f.User, first.NextOffset!.Value);
        Assert.Equal(56, first.Total); Assert.Equal(50, first.Items.Count); Assert.Equal(6, second.Items.Count); Assert.Null(second.NextOffset);
        Assert.Equal(56, first.Items.Concat(second.Items).Select(x => x.Key).Distinct().Count());
        Assert.Equal("Project", first.Items[0].Category);
    }

    [Fact]
    public async Task ExecutingWorkPrecedesQueuedAndStaleProjectWorkAcrossPages()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        // A leased project can still be waiting on provider capacity.
        var queued = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        (await f.Db.AgentRunLogs.SingleAsync(x => x.Id == queued)).Status = "Queued";
        for (var i = 0; i < 55; i++)
            f.Db.AgentRunLogs.Add(new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org,
                EmployeeId = f.Agent.Id, AgentInstallationId = f.Agent.AgentInstallationId,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(i), Status = "Running" });
        var running = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org,
            EmployeeId = f.Agent.Id, AgentInstallationId = f.Agent.AgentInstallationId,
            StartedAt = DateTimeOffset.UtcNow, Status = "Running" };
        f.Db.Add(running); await f.Db.SaveChangesAsync();
        var first = await f.Service.ReadAsync(f.Org, f.User);
        var second = await f.Service.ReadAsync(f.Org, f.User, first.NextOffset!.Value);
        Assert.Equal(running.Id, first.Items[0].ModelRunId);
        Assert.Equal("Executing", first.Items[0].State);
        Assert.Equal("Waiting", first.Items[1].State);
        Assert.All(first.Items.Skip(2).Concat(second.Items), row => Assert.Equal("Unconfirmed", row.State));
        Assert.Equal(57, first.Items.Concat(second.Items).Select(x => x.Key).Distinct().Count());
    }

    private sealed class Grants : IScopedActionAuthorizationService
    {
        public bool Allow { get; set; } = true;
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid org, GrantSubjectKind subjectKind, Guid subject,
            string action, GrantScopeKind scope, Guid? id, CancellationToken token = default) => Task.FromResult(new ScopedAuthorizationDecision(Allow, action));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public CSweetDbContext Db { get; }
        public Grants Grants { get; } = new();
        public CurrentActivityService Service { get; }
        public AuditEventWriter Writer { get; }
        public OrganizationUser Owner { get; private set; } = null!;
        public OrganizationUser Agent { get; private set; } = null!;
        public WorkTask Ticket { get; private set; } = null!;
        public AgentWorkAttempt Attempt { get; private set; } = null!;
        private readonly ServiceProvider services;
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        public Fixture()
        {
            var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new(options, protection);
            services = new ServiceCollection().AddScoped(_ => new CSweetDbContext(options, protection)).BuildServiceProvider();
            Writer = new(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
            Service = new(Db, Grants, new(Db, protection), TimeProvider.System);
        }
        public async Task SeedAsync()
        {
            var now = DateTimeOffset.UtcNow;
            Owner = new() { Id = Guid.NewGuid(), OrganizationId = Org, ApplicationUserId = User, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
            Agent = new() { Id = Guid.NewGuid(), OrganizationId = Org, AgentInstallationId = Guid.NewGuid(), DisplayName = "Test engineer", EmployeeType = EmployeeType.Agent };
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Guid.NewGuid(), Name = "Project board" };
            Ticket = new() { Id = Guid.NewGuid(), OrganizationId = Org, BoardId = board.Id, Title = "Implement keyboard navigation", Status = WorkTaskStatus.Running,
                AssignedEmployeeId = Agent.Id, AssignedAgentInstallationId = Agent.AgentInstallationId, ClaimEventId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
            Db.AddRange(Owner, Agent, board, Ticket); await Db.SaveChangesAsync();
            var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = Org.ToString(), AgentInstallationId = Agent.AgentInstallationId.Value,
                Kind = AgentWorkKind.Event, SourceId = Ticket.ClaimEventId.ToString(), Status = AgentWorkStatus.Leased, AttemptCount = 1, CreatedAt = now };
            Db.Add(work); await Db.SaveChangesAsync();
            Attempt = new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1, ClaimedAt = now, LeaseExpiresAt = now.AddMinutes(3) };
            Db.Add(Attempt); await Db.SaveChangesAsync();
        }
        public async Task<Guid> RunAsync(Guid attempt, Guid? task)
        {
            var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = Org, EmployeeId = Agent.Id, AgentInstallationId = Agent.AgentInstallationId,
                AgentWorkAttemptId = attempt, WorkItemId = task, StartedAt = DateTimeOffset.UtcNow, Status = "Running" };
            Db.Add(run); await Db.SaveChangesAsync(); return run.Id;
        }
        public async Task<AgentWorkItem> QueueTicketAsync(string source)
        {
            Db.WorkExecutionContexts.RemoveRange(await Db.WorkExecutionContexts.ToListAsync());
            Db.AgentWorkAttempts.Remove(Attempt); await Db.SaveChangesAsync();
            var board = await Db.WorkBoards.SingleAsync();
            board.Kind = WorkBoardKind.Personal; board.OwnerOrganizationUserId = Agent.Id;
            Ticket.Status = WorkTaskStatus.Ready; Ticket.ClaimEventId = null;
            Ticket.Description = "Check the proposed team against the project's staffing requirements.";
            var work = await Db.AgentWorkItems.SingleAsync();
            work.Status = AgentWorkStatus.Pending; work.AttemptCount = 0;
            work.SourceType = source == "chief-chat" ? "chat-turn" : "platform-event";
            work.SourceId = Guid.NewGuid().ToString("D");
            if (source is "personal" or "assigned" or "discussion")
            {
                work.Name = source == "personal" ? Wire.PersonalTodoEvents.Available : source == "assigned" ? Wire.WorkItemEvents.Assigned : "com.csweet.work.item.discussion.changed.v1";
                if (source != "personal") { board.Kind = WorkBoardKind.Standard; board.OwnerOrganizationUserId = null; }
                object payload = source == "personal" ? new Wire.PersonalTodoAvailableEvent(Agent.Id, board.Id, Ticket.Id) :
                    source == "assigned" ? new Wire.WorkItemAssignedEvent(board.Id, Ticket.Id, Ticket.AssignmentRevision, Agent.AgentInstallationId!.Value) :
                    new { boardId = board.Id, itemId = Ticket.Id, recipientEmployeeId = Agent.Id };
                Db.AgentPlatformEventOutbox.Add(new() { Id = Guid.Parse(work.SourceId), OrganizationId = Org,
                    TargetInstallationId = Agent.AgentInstallationId, EventType = work.Name,
                    DataJson = JsonSerializer.Serialize(payload,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            }
            else
            {
                work.Name = "com.csweet.agent.attention.review-due.v1";
                Ticket.IsExecutable = false; Ticket.CreatedByOrganizationUserId = Agent.Id;
                Ticket.CreationIdempotencyKey = source == "chief-chat" ? $"chief-work:chat:{Guid.Parse(work.SourceId):N}" : $"chief-work:delivery:{work.Id:N}";
            }
            await Db.SaveChangesAsync();
            return work;
        }
        public async Task<WorkDeliveryExecution> DeliveryAsync(string stageKey, Guid? taskId = null)
        {
            var board = await Db.WorkBoards.SingleAsync();
            var plan = new WorkDeliveryPlan { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = board.WorkstreamId!.Value, Name = "First playable" };
            var execution = new WorkDeliveryExecution { Id = Guid.NewGuid(), PlanId = plan.Id, Plan = plan,
                BoardId = board.Id, WorkItemId = taskId, Scope = taskId.HasValue ? "Epic" : "Release", CurrentStageKey = stageKey };
            var stage = new WorkStageExecution { Id = Guid.NewGuid(), DeliveryExecutionId = execution.Id, DeliveryExecution = execution,
                StageKey = stageKey, OrganizationUserId = Agent.Id, AgentInstallationId = Agent.AgentInstallationId };
            var work = await Db.AgentWorkItems.SingleAsync();
            work.SourceType = "WorkDeliveryStage"; work.SourceId = stage.Id.ToString(); work.Name = "work.execution.run";
            // Aggregate deliveries can have no task focus, even during an active review.
            foreach (var context in await Db.WorkExecutionContexts.ToListAsync()) { context.RootWorkItemId = null; context.WorkItemId = null; }
            var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, StageExecution = stage,
                AgentWorkItemId = work.Id, Attempt = 1 };
            Db.AddRange(plan, execution, stage, attempt); await Db.SaveChangesAsync();
            return execution;
        }
        public async Task<Guid> ProgressAsync(Guid task, string? message, string kind = "workspace-tool", bool useDelta = false)
        {
            var record = new AgentWorkProgress { Id = Guid.NewGuid(), AgentWorkItemId = Attempt.AgentWorkItemId,
                AgentWorkAttemptId = Attempt.Id, Sequence = await Db.AgentWorkProgress.CountAsync() + 1,
                OccurredAt = DateTimeOffset.UtcNow, ProtectedValue = protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(
                    JsonSerializer.SerializeToUtf8Bytes(new { itemId = task, kind, message = useDelta ? null : message, delta = useDelta ? message : null })) };
            Db.Add(record); await Db.SaveChangesAsync();
            await Writer.AppendAsync(new("agent.work.progress", "AgentWork", OrganizationId: Org,
                EntityType: "AgentWorkProgress", EntityId: record.Id, ContentType: "application/json",
                Payload: JsonSerializer.SerializeToUtf8Bytes(new { evidence = new { contentPolicy = "memory-content-omitted-v1" } })));
            return record.Id;
        }
        public Task<Guid> ChunkAsync(Guid run, long sequence, string text) => Writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: Org,
            EntityType: "AgentRunLog", EntityId: run, ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { sequence, contents = new[] { new { kind = "reasoning", text } } })));
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await services.DisposeAsync(); }
    }
}
