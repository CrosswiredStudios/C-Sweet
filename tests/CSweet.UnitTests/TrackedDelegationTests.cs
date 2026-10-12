using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Security;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class TrackedDelegationTests
{
    [Fact]
    public async Task AttentionThresholdsRemainQuietAfterReportingUntilSubstantiveChange()
    {
        var clock = new TestClock(); await using var f = await Fixture.Create(time: clock);
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        clock.Now += TimeSpan.FromHours(1); await f.Service.ReconcileAsync();
        var pending = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        Assert.Equal("Attention", pending.Update.Kind);
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(d.Id, pending.Update.Id, "Gabriel has not acknowledged yet.", "attention"));
        clock.Now += TimeSpan.FromHours(2); await f.Service.ReconcileAsync();
        Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Recheck staffing readiness.", "accept"));
        var ack = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(d.Id, ack.Update.Id, "Gabriel is checking staffing readiness.", "ack"));
        clock.Now += TimeSpan.FromHours(24); await f.Service.ReconcileAsync();
        Assert.Single((await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20)).Where(x => x.Update.Kind == "Attention"));
    }

    [Fact]
    public async Task ProgressCoalescesAtFiveMinutesWhileBlockersAreImmediate()
    {
        var clock = new TestClock(); await using var f = await Fixture.Create(time: clock);
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Recheck staffing.", "accept"));
        var ack = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        d = await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(d.Id, ack.Update.Id, "Gabriel is rechecking staffing.", "ack"));
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Progress", "Checking gateway.", "progress1"));
        clock.Now += TimeSpan.FromMinutes(2);
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Progress", "Gateway is ready; checking recovery.", "progress2"));
        Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Blocked", "Approval is needed.", "blocker"));
        Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        clock.Now += TimeSpan.FromMinutes(3); await f.Service.ReconcileAsync();
        var progress = (await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20)).Where(x => x.Update.Kind == "Progress").ToArray();
        Assert.Single(progress); Assert.Equal("Gateway is ready; checking recovery.", progress[0].Update.Summary);
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(d.Id, progress[^1].Update.Id, "Gabriel confirmed the gateway and is checking recovery.", "progress"));
        Assert.DoesNotContain(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20), x => x.Update.Kind == "Progress");
    }

    [Fact]
    public async Task EmployeeDeactivationPreservesResponsibilitiesAndNotifiesManagerOnce()
    {
        await using var f = await Fixture.Create();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Recheck staffing.", "accept"));
        f.Gabriel.IsActive = false; await f.Db.SaveChangesAsync(); await f.Service.ReconcileAsync();
        var notifications = await f.Db.UserNotifications.CountAsync(); Assert.True(notifications > 0);
        await f.Service.ReconcileAsync(); Assert.Equal(notifications, await f.Db.UserNotifications.CountAsync());
        var saved = await f.Db.TrackedDelegations.SingleAsync();
        Assert.Equal("AttentionRequired", saved.Status); Assert.Equal(f.Gabriel.Id, saved.RecipientId); Assert.Equal(f.Naomi.Id, saved.CoordinatorId);
        Assert.Equal(WorkTaskStatus.Backlog, (await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.WorkItemId)).Status);
        Assert.All(await f.Db.WorkTrackingSubscriptions.ToListAsync(), x => Assert.Null(x.UnfollowedAt));
    }

    [Fact]
    public async Task CompletedBeforeBindingIsRecoverableAndCancelledWorkCannotBeReportedSuccessful()
    {
        await using var f = await Fixture.Create();
        var todo = await new PersonalTodoService(f.Db, TimeProvider.System).AddAsync(f.Org, new(f.Gabriel.Id, f.Gabriel.AgentInstallationId), new("Recover staffing", null, "Medium", null, "existing"));
        var item = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == todo.Id); item.Status = WorkTaskStatus.Cancelled; item.Revision++; await f.Db.SaveChangesAsync();
        await f.Service.ReconcileAsync(); // terminal event precedes delegation binding
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Review existing recovery result.", "accept", item.Id));
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(), x => x.IdempotencyKey.StartsWith("delegation-binding-review:"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Completed", "Ready", "false")));
        Assert.Equal("Active", (await f.Db.TrackedDelegations.SingleAsync()).Status);
    }

    private sealed class TestClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task IndependentFollowCanBeRemovedAfterAccessIsRevokedWithoutSharingWork()
    {
        await using var f = await Fixture.Create();
        var item = await new PersonalTodoService(f.Db, TimeProvider.System).AddAsync(f.Org, new(f.Gabriel.Id, f.Gabriel.AgentInstallationId), new("Recover staffing", null, "Medium", null, "work"));
        await f.Service.FollowAsync(f.Org, f.Matt.Id, new(item.Id, "follow"), true);
        foreach (var grant in f.Db.ScopedActionGrants.Where(x => x.SubjectId == f.Matt.Id)) grant.RevokedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadTrackingAsync(f.Org, f.Matt.Id, item.Id));
        var removed = await f.Service.FollowAsync(f.Org, f.Matt.Id, new(item.Id, "remove"), false);
        Assert.False(removed.Following); Assert.True(removed.Limited); Assert.Null(removed.WorkItemUrl); Assert.Empty(removed.Updates);
        Assert.NotNull((await f.Db.WorkTrackingSubscriptions.SingleAsync()).UnfollowedAt);
    }

    [Fact]
    public async Task BindingRealWorkRetiresOnlyTheNonExecutablePlaceholderWithoutStartingAnotherTurn()
    {
        await using var f = await Fixture.Create();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Check readiness.", "accept"));
        var placeholder = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.WorkItemId);
        var real = await new PersonalTodoService(f.Db, TimeProvider.System).AddAsync(f.Org, new(f.Gabriel.Id, f.Gabriel.AgentInstallationId), new("Retry staffing", null, "Medium", null, "real-work"));
        var turn = d.ExecutionTurnId;
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Using the recovery work item.", "bind", real.Id));
        Assert.Equal(real.Id, d.WorkItemId); Assert.NotNull(placeholder.ArchivedAt); Assert.False(placeholder.IsExecutable);
        Assert.Equal(turn, d.ExecutionTurnId); Assert.Single(await f.Db.ChatTurns.ToListAsync());
        Assert.Null((await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == real.Id)).ArchivedAt);
    }

    [Fact]
    public async Task CanonicalTransferPreservesTrackingAndRechecksDestinationAccess()
    {
        await using var f = await Fixture.Create();
        f.Matt.ApplicationUserId = Guid.NewGuid(); f.Matt.PermissionLevel = OrganizationPermissionLevel.Owner;
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Check readiness.", "accept"));
        var item = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.WorkItemId);
        var target = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org, Name = "Delivery", Key = "DEL", Kind = WorkBoardKind.Standard };
        target.Columns.Add(new() { Id = Guid.NewGuid(), BoardId = target.Id, Name = "To do", Category = WorkBoardColumnCategory.ToDo });
        f.Db.WorkBoards.Add(target); await f.Db.SaveChangesAsync();
        await f.Service.FollowAsync(f.Org, f.Matt.Id, new(item.Id, "follow"), true);
        var service = new WorkItemCollaborationService(f.Db, new ScopedActionAuthorizationService(f.Db), new TestAuditEventWriter());
        var moved = await service.TransferAsync(f.Org, item.BoardId!.Value, item.Id, f.Matt.ApplicationUserId.Value, new(target.Id, null, item.Revision, "transfer"));
        Assert.Equal(item.Id, moved!.Id); Assert.Equal(target.Id, item.BoardId);
        var limited = await f.Service.ReadTrackingAsync(f.Org, f.Gabriel.Id, item.Id);
        Assert.True(limited.Limited); Assert.Null(limited.WorkItemUrl);
        Assert.All(await f.Db.WorkTrackingSubscriptions.ToListAsync(), x => Assert.Equal(item.Id, x.WorkItemId));
        Assert.Equal(item.Id, (await f.Service.ReadAsync(f.Org, f.Naomi.Id, new(d.Id))).Single().WorkItemId);
        f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = f.Gabriel.AgentInstallationId!.Value, ScopeKind = GrantScopeKind.Board, ScopeId = target.Id, Action = WorkItemActions.Read });
        await f.Db.SaveChangesAsync(); Assert.False((await f.Service.ReadTrackingAsync(f.Org, f.Gabriel.Id, item.Id)).Limited);
    }
    [Fact]
    public async Task LimitedTrackingNeverExposesPrivateTaskAndManualFollowingRechecksRevocation()
    {
        await using var f = await Fixture.Create();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Rechecking staffing readiness.", "accept"));
        var task = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.WorkItemId);
        task.Description = "Private task details must not be shared"; await f.Db.SaveChangesAsync();
        foreach (var existingGrant in await f.Db.ScopedActionGrants.Where(x => x.SubjectId == f.Matt.Id && x.ScopeId == task.BoardId).ToListAsync())
            existingGrant.RevokedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        var limited = await f.Service.ReadTrackingAsync(f.Org, f.Matt.Id, task.Id);
        Assert.True(limited.Limited); Assert.Null(limited.WorkItemUrl);
        Assert.DoesNotContain(limited.Updates, x => x.Summary.Contains("Private task"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FollowAsync(f.Org, f.Matt.Id, new(task.Id, "follow"), true));
        var grant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.OrganizationUser,
            SubjectId = f.Matt.Id, ScopeKind = GrantScopeKind.Board, ScopeId = task.BoardId!.Value, Action = CSweet.Contracts.WorkManagement.PersonalTodoActions.Read };
        f.Db.ScopedActionGrants.Add(grant); f.Matt.PermissionLevel = OrganizationPermissionLevel.Owner; await f.Db.SaveChangesAsync();
        Assert.True((await f.Service.FollowAsync(f.Org, f.Matt.Id, new(task.Id, "follow-ok"), true)).Following);
        await f.Service.FollowAsync(f.Org, f.Matt.Id, new(task.Id, "unfollow"), false);
        Assert.False((await f.Service.FollowAsync(f.Org, f.Matt.Id, new(task.Id, "follow-ok"), true)).Following); // replay must not resurrect a follow.
        await f.Service.FollowAsync(f.Org, f.Matt.Id, new(task.Id, "refollow"), true);
        grant.RevokedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        Assert.True((await f.Service.ReadTrackingAsync(f.Org, f.Matt.Id, task.Id)).Limited);
        Assert.False((await f.Service.FollowAsync(f.Org, f.Matt.Id, new(task.Id, "unfollow-revoked"), false)).Following);
        var ack = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        var report = new PublishDelegationReportRequest(d.Id, ack.Update.Id, "Gabriel is rechecking staffing readiness.", "initial-report");
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, report);
        f.Source.Content += " This instruction is withdrawn."; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RespondAsync(f.Org, f.Gabriel.Id,
            new(d.Id, d.Revision, "Accepted", "Rechecking staffing readiness.", "accept")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.PublishAsync(f.Org, f.Naomi.Id, report));
    }

    [Fact]
    public async Task NestedDeclineReportsUpstreamWithoutClosingParentAndCancellationLeavesExecutionIntact()
    {
        await using var f = await Fixture.Create();
        var third = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, DisplayName = "Third", EmployeeType = EmployeeType.Agent,
            AgentInstallationId = Guid.NewGuid(), IsActive = true, ReportsToOrganizationUserId = f.Gabriel.Id };
        f.Db.CoreOrganizationUsers.Add(third); await f.Db.SaveChangesAsync();
        var parent = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        parent = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(parent.Id, parent.Revision, "Accepted", "Rechecking staffing.", "accept"));
        var child = await f.Service.CreateAsync(f.Org, f.Gabriel.Id, new(third.Id, parent.Instruction, parent.IntendedOutcome,
            parent.RecipientConversationId, parent.ExecutionMessageId!.Value, "child", ParentDelegationId: parent.Id));
        child = await f.Service.RespondAsync(f.Org, third.Id, new(child.Id, child.Revision, "Declined", "I cannot perform this recovery.", "decline"));
        var decline = (await f.Service.PendingAsync(f.Org, f.Gabriel.Id, 20)).Single(x => x.DelegationId == child.Id);
        await f.Service.PublishAsync(f.Org, f.Gabriel.Id, new(child.Id, decline.Update.Id, "The downstream recipient cannot perform recovery.", "child-report"));
        Assert.Single(await f.Db.CoreConversationMessages.Where(x => x.IdempotencyKey == $"delegation-report:{decline.Update.Id:N}").ToListAsync());
        var upstream = (await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20)).Single(x => x.Update.Kind == "Blocked");
        parent = await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(parent.Id, upstream.Update.Id, "Gabriel needs another recovery path.", "parent-report"));
        Assert.Equal("Active", parent.Status);
        var item = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == parent.WorkItemId);
        var previousStatus = item.Status;
        parent = await f.Service.CancelAsync(f.Org, f.Naomi.Id, new(parent.Id, parent.Revision, "cancel"));
        Assert.Equal("Cancelled", parent.Status); Assert.Equal(previousStatus, item.Status);
    }

    [Fact]
    public async Task IndependentFollowingHasBoundedDiscoveryAndDurableProcessingReceipts()
    {
        await using var f = await Fixture.Create();
        var todos = new PersonalTodoService(f.Db, TimeProvider.System);
        var item = await todos.AddAsync(f.Org, new(f.Gabriel.Id, f.Gabriel.AgentInstallationId), new("Recover staffing", null, "Medium", null, "work"));
        await f.Service.FollowAsync(f.Org, f.Gabriel.Id, new(item.Id, "follow"), true);
        var update = await f.Service.ProgressAsync(f.Org, f.Gabriel.Id, new(item.Id, item.Revision, "Decision", "Staffing approval is needed.", "decision"));
        Assert.Single(await f.Service.PendingAsync(f.Org, f.Gabriel.Id, 1));
        await f.Service.ProcessedAsync(f.Org, f.Gabriel.Id, new(item.Id, update.Id));
        await f.Service.ProcessedAsync(f.Org, f.Gabriel.Id, new(item.Id, update.Id));
        Assert.Empty(await f.Service.PendingAsync(f.Org, f.Gabriel.Id, 20));
        Assert.Single(await f.Db.WorkTrackingOperationReceipts.Where(x => x.Operation == "processed").ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.PendingAsync(f.Org, f.Gabriel.Id, 51));
    }
    [Fact]
    public async Task StaffingJourneyPersistsCommitmentAndSharedReceiptsWithoutReplyLoopsOrFalseCompletion()
    {
        await using var f = await Fixture.Create();
        var request = f.Request();
        var created = await f.Service.CreateAsync(f.Org, f.Naomi.Id, request);
        Assert.Equal(created.Id, (await f.Service.CreateAsync(f.Org, f.Naomi.Id, request)).Id);
        Assert.Equal("AwaitingAcknowledgement", created.Status);
        Assert.True(created.IsDirective);
        Assert.False((await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == created.CommitmentItemId)).IsExecutable);
        Assert.Empty(await f.Db.ChatTurns.ToListAsync());
        Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == DelegationCapabilities.Received).ToListAsync());
        var acceptedRequest = new DelegationResponseRequest(created.Id, created.Revision, "Accepted", "I will recheck delivery staffing.", "accept");
        var accepted = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, acceptedRequest);
        Assert.Equal(accepted.WorkItemId, (await f.Service.RespondAsync(f.Org, f.Gabriel.Id, acceptedRequest)).WorkItemId);
        Assert.Equal("Active", accepted.Status);
        Assert.NotNull(accepted.WorkItemId);
        Assert.Single(await f.Db.ChatTurns.ToListAsync());
        Assert.Equal(2, await f.Db.WorkTrackingSubscriptions.CountAsync());
        var ack = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(created.Id, ack.Update.Id, "Gabriel is rechecking delivery staffing.", "report-ack"));
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(created.Id, ack.Update.Id, "Gabriel is rechecking delivery staffing.", "report-ack"));
        var report = Assert.Single(await f.Db.CoreConversationMessages.Where(x => x.IdempotencyKey == $"delegation-report:{ack.Update.Id:N}").ToListAsync());
        Assert.Contains("[Track work]", report.Content);
        Assert.Equal(CommunicationDeliveryIntent.Inform, report.DeliveryIntent);
        Assert.Null(report.ChatTurnId);
        accepted = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(created.Id))).Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RespondAsync(f.Org, f.Gabriel.Id,
            new(created.Id, accepted.Revision, "Completed", "Requeued staffing.", "false-complete")));
        var blocked = await f.Service.RespondAsync(f.Org, f.Gabriel.Id,
            new(created.Id, accepted.Revision, "Blocked", "Approval is required before staffing can continue.", "blocked"));
        Assert.Equal("Blocked", blocked.Status);
        var blocker = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(created.Id, blocker.Update.Id, "Gabriel needs your staffing approval.", "report-blocker"));
        Assert.Equal("Blocked", (await f.Db.TrackedDelegations.SingleAsync()).Status);
        blocked = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(created.Id))).Single();
        var task = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == accepted.WorkItemId);
        task.Status = WorkTaskStatus.Completed; task.Revision++; task.ResultSummary = "Approved staffing is ready.";
        await f.Db.SaveChangesAsync();
        var final = await f.Service.RespondAsync(f.Org, f.Gabriel.Id,
            new(created.Id, blocked.Revision, "Completed", "Approved staffing is ready.", "result"));
        Assert.Equal("ResultReady", final.Status);
        var result = (await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20)).Single(x => x.Update.Kind == "Completed");
        var closed = await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(created.Id, result.Update.Id, "Gabriel confirmed staffing is ready.", "report-final"));
        Assert.Equal("Completed", closed.Status);
        Assert.Equal(WorkTaskStatus.Completed, (await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == closed.CommitmentItemId)).Status);
        Assert.All(await f.Db.WorkTrackingSubscriptions.ToListAsync(), x => Assert.NotNull(x.UnfollowedAt));
    }

    [Theory]
    [InlineData("Clarification", "AwaitingClarification")]
    [InlineData("Declined", "Declined")]
    public async Task PeerDecisionDoesNotCreateExecutionBeforeAcceptance(string response, string expected)
    {
        await using var f = await Fixture.Create();
        f.Gabriel.ReportsToOrganizationUserId = f.Matt.Id; await f.Db.SaveChangesAsync();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        Assert.False(d.IsDirective);
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, response, "Please clarify the scope.", "decision"));
        Assert.Equal(expected, d.Status); Assert.Null(d.WorkItemId); Assert.Empty(await f.Db.ChatTurns.ToListAsync());
    }

    [Fact]
    public async Task SourceAmendmentFencesDelegationAndNotifiesOnlyOnce()
    {
        await using var f = await Fixture.Create();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        f.Source.Content = "Changed instruction"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RespondAsync(f.Org, f.Gabriel.Id,
            new(d.Id, d.Revision, "Accepted", "I will retry.", "accept")));
        await f.Service.ReconcileAsync(); var count = await f.Db.UserNotifications.CountAsync();
        await f.Service.ReconcileAsync(); Assert.Equal(count, await f.Db.UserNotifications.CountAsync());
        Assert.Equal("AttentionRequired", (await f.Db.TrackedDelegations.SingleAsync()).Status);
        Assert.Empty(await f.Db.ChatTurns.ToListAsync());
    }

    [Fact]
    public async Task ReportingAuthorityChangeFencesAnExistingDirective()
    {
        await using var f = await Fixture.Create();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request()); Assert.True(d.IsDirective);
        f.Naomi.ReportsToOrganizationUserId = null; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RespondAsync(f.Org, f.Gabriel.Id,
            new(d.Id, d.Revision, "Accepted", "I will retry.", "accept")));
        await f.Service.ReconcileAsync(); Assert.Equal("AttentionRequired", (await f.Db.TrackedDelegations.SingleAsync()).Status);
        Assert.Empty(await f.Db.ChatTurns.ToListAsync());
    }

    [Fact]
    public async Task ReopenedWorkSupersedesAnUndeliveredFinalReportAndKeepsTrackingOpen()
    {
        await using var f = await Fixture.Create();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Check readiness.", "accept"));
        var ack = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        d = await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(d.Id, ack.Update.Id, "Gabriel is checking readiness.", "ack"));
        var item = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.WorkItemId); item.Status = WorkTaskStatus.Completed; item.Revision++; await f.Db.SaveChangesAsync();
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Completed", "Staffing ready.", "final"));
        var final = Assert.Single(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        item.Status = WorkTaskStatus.Ready; item.Revision++; await f.Db.SaveChangesAsync();
        d = await f.Service.PublishAsync(f.Org, f.Naomi.Id, new(d.Id, final.Update.Id, "Staffing is ready.", "report"));
        Assert.Equal("Active", d.Status);
        Assert.DoesNotContain(await f.Db.CoreConversationMessages.ToListAsync(), x => x.IdempotencyKey == $"delegation-report:{final.Update.Id:N}");
        Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        Assert.False((await f.Db.WorkTrackingUpdates.SingleAsync(x => x.Id == final.Update.Id)).ReportedAt.HasValue);
        Assert.NotEqual(WorkTaskStatus.Completed, (await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.CommitmentItemId)).Status);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public required CSweetDbContext Db { get; init; }
        public required TrackedDelegationService Service { get; init; }
        public required OrganizationUser Matt { get; init; }
        public required OrganizationUser Naomi { get; init; }
        public required OrganizationUser Gabriel { get; init; }
        public required ConversationMessage Source { get; init; }
        private bool OwnsDb { get; init; } = true;
        public Guid Org => Matt.OrganizationId;
        public DelegateWithTrackingRequest Request() => new(Gabriel.Id, "Retry staffing: gateway and recovery fixes installed",
            "Resolve staffing readiness after the gateway and recovery fixes.", Source.ConversationId, Source.Id, "source");
        public static async Task<Fixture> Create(CSweetDbContext? context = null, TimeProvider? time = null)
        {
            var db = context ?? new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var org = new Organization { Id = Guid.NewGuid(), Name = "Delegation test" };
            OrganizationUser Person(string name, EmployeeType kind) => new() { Id = Guid.NewGuid(), OrganizationId = org.Id,
                DisplayName = name, EmployeeType = kind, IsActive = true, AgentInstallationId = kind == EmployeeType.Agent ? Guid.NewGuid() : null };
            var matt = Person("Matt", EmployeeType.Human); var naomi = Person("Naomi Chen", EmployeeType.Agent); var gabriel = Person("Gabriel Reyes", EmployeeType.Agent);
            naomi.ReportsToOrganizationUserId = matt.Id; gabriel.ReportsToOrganizationUserId = naomi.Id;
            var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = org.Id, Kind = ConversationKind.DirectHumanAgent, AgentOrganizationUserId = naomi.Id };
            chat.Participants.Add(new() { Id = Guid.NewGuid(), OrganizationUserId = matt.Id });
            chat.Participants.Add(new() { Id = Guid.NewGuid(), OrganizationUserId = naomi.Id });
            var source = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat.Id, SenderOrganizationUserId = matt.Id,
                Content = "Please tell @Gabriel Reyes to \"Retry staffing: gateway and recovery fixes installed\"", Role = ConversationRole.User };
            if (db.Database.IsRelational())
            {
                var packageSource = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/delegation" };
                var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = packageSource.Id, AgentId = "delegation-test", Version = "1.0.0", ManifestJson = "{}" };
                db.AddRange(packageSource, package);
                foreach (var person in new[] { naomi, gabriel }) db.AgentInstallations.Add(new() { Id = person.AgentInstallationId!.Value,
                    PackageVersionId = package.Id, BusinessId = org.Id.ToString("D"), InstallationKey = Guid.NewGuid() });
            }
            db.AddRange(org, matt, naomi, gabriel, chat, source); await db.SaveChangesAsync();
            var todos = new PersonalTodoService(db, TimeProvider.System);
            return new() { Db = db, Service = new(db, todos, time ?? TimeProvider.System, new ChatTurnService(db)), Matt = matt, Naomi = naomi, Gabriel = gabriel, Source = source, OwnsDb = context is null };
        }
        public ValueTask DisposeAsync() => OwnsDb ? Db.DisposeAsync() : ValueTask.CompletedTask;
    }
}
