using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ResourceChangeServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CarriedForwardCorrectionUsesCurrentApprovalCancelsLineageHiresAndPreservesCompletedHires(bool openCurrentCard)
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.ManagerInstallationId);
        manager.ApplicationUserId = Guid.NewGuid();
        var resources = new ResourceChangeService(db, new TestAuditEventWriter());
        var first = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "initial"));
        (await db.ResourceChangeRequests.SingleAsync()).Status = ResourceChangeRequestStatus.Approved;
        await db.SaveChangesAsync();
        async Task<ResourceChangeRequestResponse> Advance(ResourceChangeRequestResponse previous, string key)
        {
            var next = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId,
                Proposal(f, key) with { ChatTurnId = Guid.Empty, SupersedesRequestId = previous.Id,
                    Roles = previous.Roles.Select(x => x with { Purpose = x.Purpose + " Revised." }).ToArray() });
            return await resources.DecideForInstallationAsync(f.OrganizationId, f.ManagerInstallationId, new(next.Id, "Approve", "Approve exact package", key + "-approve"));
        }
        var middle = await Advance(first, "middle"); var current = await Advance(middle, "current");
        // Like Prism Break: the current approval retains a disputed position, but the
        // visible hiring card still references the first approval. Revisions can coincide.
        Assert.Equal(first.ContextRevision, current.ContextRevision);
        var role = first.Roles[0].RoleKey;
        WorkforcePlan Slot(Guid source, string key, bool filled = false) => new() { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId,
            SourceResourceChangeRequestId = source, RequestingInstallationId = f.ManagerInstallationId, RoleKey = key,
            Title = key, Headcount = 1, FulfilledHeadcount = filled ? 1 : 0, Status = filled ? ProposalStatus.Approved : ProposalStatus.Pending };
        var originalCard = Slot(first.Id, role); var middleCard = Slot(middle.Id, role); var currentCard = Slot(current.Id, role);
        var completed = Slot(first.Id, first.Roles[1].RoleKey, true);
        var workflow = new StaffingActionProposal { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, WorkforcePlanId = middleCard.Id,
            Status = ProposalStatus.Approved };
        var operation = new AgentHireOperation { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, WorkflowId = workflow.Id,
            Status = AgentHireOperationStatus.Building };
        db.AddRange(originalCard, middleCard, currentCard, completed, workflow, operation); await db.SaveChangesAsync();
        var service = new StaffingCorrectionService(db);
        var selected = openCurrentCard ? currentCard : originalCard;
        var response = HiringCard(selected);
        var action = Assert.Single(await service.WithActionsAsync(f.OrganizationId, manager.ApplicationUserId.Value, [response], default));
        Assert.Equal(selected.SourceResourceChangeRequestId, action.SourceResourceChangeRequestId); // Historical attribution is intact.
        Assert.Equal(current.Id, action.CorrectionSourceRequestId);
        Assert.Equal(current.ContextRevision, action.CorrectionSourceRevision);
        var reason = string.Concat(Enumerable.Repeat("Correct the misclassified unfilled capability and preserve current employees. ", 100));
        var command = new RequestStaffingCorrection(current.ContextRevision, reason, "correct-carried-slot") { ExpectedSourceRequestId = current.Id };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RequestAsync(f.OrganizationId, selected.Id,
            manager.ApplicationUserId.Value, command with { ExpectedSourceRequestId = first.Id }));
        if (!openCurrentCard)
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RequestAsync(f.OrganizationId, selected.Id,
                manager.ApplicationUserId.Value, command with { ExpectedSourceRequestId = null }));
        var correction = await service.RequestAsync(f.OrganizationId, selected.Id, manager.ApplicationUserId.Value, command);
        Assert.Equal(current.Id, correction.SourceRequestId); Assert.Equal(f.RequesterId, correction.RequesterOrganizationUserId);
        Assert.Equal(reason.Trim(), correction.Reason);
        Assert.All(new[] { originalCard, middleCard, currentCard }, x => Assert.Equal(ProposalStatus.Cancelled, x.Status));
        Assert.Equal(ProposalStatus.Approved, completed.Status); Assert.Equal(1, completed.FulfilledHeadcount);
        Assert.Equal(AgentHireOperationStatus.Cancelled, operation.Status);
        Assert.Equal(reason.Trim(), workflow.DecisionComment);
        Assert.Contains(await db.AgentPlatformEventOutbox.ToListAsync(), x => x.TargetInstallationId == f.RequesterInstallationId && x.DataJson.Contains(current.Id.ToString()));
        Assert.Single((await resources.ReadForInstallationAsync(f.OrganizationId, f.RequesterInstallationId, new())).Corrections);
        var replacement = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId,
            Proposal(f, "corrected") with { ChatTurnId = Guid.Empty, SupersedesRequestId = current.Id, CorrectionId = correction.Id,
                Roles = [current.Roles[1], current.Roles[0] with { RoleKey = "technical-planning" }],
                Evidence = [new("staffing-correction", $"{correction.Id:N}:{correction.Revision}", correction.Reason)] });
        Assert.Equal("Pending", replacement.Status);
        await resources.DecideForInstallationAsync(f.OrganizationId, f.ManagerInstallationId, new(replacement.Id, "Approve", "Approve corrected plan", "approve-corrected"));
        db.ChangeTracker.Clear();
        Assert.Equal("Completed", (await db.StaffingCorrections.SingleAsync()).Status);
        var replay = await new StaffingCorrectionService(db).RequestAsync(f.OrganizationId, selected.Id, manager.ApplicationUserId.Value, command);
        Assert.Equal(correction.Id, replay.Id); Assert.Equal("Completed", replay.Status);
        Assert.Single(await db.StaffingCorrections.ToListAsync());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("removed")]
    [InlineData("different-team")]
    [InlineData("different-project")]
    [InlineData("foreign")]
    [InlineData("branch")]
    [InlineData("cycle")]
    [InlineData("unrelated")]
    [InlineData("fulfilled")]
    public async Task CorrectionDoesNotGuessAcrossInvalidOrUnapprovedLineage(string scenario)
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.ManagerInstallationId);
        manager.ApplicationUserId = Guid.NewGuid();
        var resources = new ResourceChangeService(db, new TestAuditEventWriter());
        var first = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "initial"));
        var source = await db.ResourceChangeRequests.SingleAsync(); source.Status = ResourceChangeRequestStatus.Superseded;
        var role = first.Roles[0].RoleKey;
        ResourceChangeRequestRecord Next() => new() { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SupersedesRequestId = first.Id,
            Status = ResourceChangeRequestStatus.Approved, ContextRevision = first.ContextRevision, RequesterOrganizationUserId = f.RequesterId,
            ManagerOrganizationUserId = manager.Id, Roles = [new() { Id = Guid.NewGuid(), RoleKey = role, IsDesired = true, Headcount = 1 }] };
        var current = Next();
        switch (scenario)
        {
            case "pending": current.Status = ResourceChangeRequestStatus.Pending; break;
            case "removed": current.Roles.Clear(); break;
            case "different-team": current.TeamId = Guid.NewGuid(); break;
            case "different-project": current.WorkstreamId = Guid.NewGuid(); break;
            case "foreign": current.OrganizationId = Guid.NewGuid(); break;
            case "branch": db.ResourceChangeRequests.Add(Next()); break;
            case "cycle": current.Status = ResourceChangeRequestStatus.Superseded; source.SupersedesRequestId = current.Id; break;
            case "unrelated": current.SupersedesRequestId = null; break;
            case "fulfilled": db.WorkforcePlans.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId,
                SourceResourceChangeRequestId = current.Id, RoleKey = role, Headcount = 1, FulfilledHeadcount = 1, Status = ProposalStatus.Approved }); break;
        }
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SourceResourceChangeRequestId = source.Id,
            RoleKey = role, Headcount = 1, Status = ProposalStatus.Pending };
        db.AddRange(current, plan); await db.SaveChangesAsync();
        var service = new StaffingCorrectionService(db);
        var action = Assert.Single(await service.WithActionsAsync(f.OrganizationId, manager.ApplicationUserId.Value, [HiringCard(plan)], default));
        Assert.Null(action.CorrectionSourceRevision); Assert.Null(action.CorrectionSourceRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(f.OrganizationId, plan.Id, manager.ApplicationUserId.Value,
            new(first.ContextRevision, "Fix the slot", "invalid") { ExpectedSourceRequestId = current.Id }));
        Assert.Empty(await db.StaffingCorrections.ToListAsync()); Assert.Equal(ProposalStatus.Pending, plan.Status);
    }

    [Fact]
    public async Task CarriedForwardCorrectionUsesCurrentManagerAndPendingCorrectionHidesEveryRelatedAction()
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.ManagerInstallationId);
        manager.ApplicationUserId = Guid.NewGuid();
        var newManager = User(f.OrganizationId, "New manager", EmployeeType.Human); newManager.ApplicationUserId = Guid.NewGuid();
        var author = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.RequesterId);
        var resources = new ResourceChangeService(db, new TestAuditEventWriter());
        var first = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "initial"));
        (await db.ResourceChangeRequests.SingleAsync()).Status = ResourceChangeRequestStatus.Superseded;
        var current = new ResourceChangeRequestRecord { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SupersedesRequestId = first.Id,
            Status = ResourceChangeRequestStatus.Approved, ContextRevision = first.ContextRevision, RequesterOrganizationUserId = f.RequesterId,
            RequesterInstallationId = f.RequesterInstallationId, ManagerOrganizationUserId = newManager.Id,
            Roles = [new() { Id = Guid.NewGuid(), RoleKey = first.Roles[0].RoleKey, IsDesired = true, Headcount = 1 }] };
        author.ReportsToOrganizationUserId = newManager.Id;
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SourceResourceChangeRequestId = first.Id,
            RoleKey = first.Roles[0].RoleKey, Headcount = 1, Status = ProposalStatus.Pending };
        db.AddRange(newManager, current, plan); await db.SaveChangesAsync();
        var service = new StaffingCorrectionService(db);
        Assert.Null(Assert.Single(await service.WithActionsAsync(f.OrganizationId, manager.ApplicationUserId.Value, [HiringCard(plan)], default)).CorrectionSourceRevision);
        var command = new RequestStaffingCorrection(current.ContextRevision, "Correct staffing", "correction") { ExpectedSourceRequestId = current.Id };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestAsync(f.OrganizationId, plan.Id, manager.ApplicationUserId.Value, command));
        await service.RequestAsync(f.OrganizationId, plan.Id, newManager.ApplicationUserId.Value, command);
        // Simulate another still-visible card from a prior read; pending state is authoritative.
        Assert.Null(Assert.Single(await service.WithActionsAsync(f.OrganizationId, newManager.ApplicationUserId.Value,
            [HiringCard(plan) with { Status = "Pending", RemainingHeadcount = 1 }], default)).CorrectionSourceRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(f.OrganizationId, plan.Id, newManager.ApplicationUserId.Value, command with { IdempotencyKey = "duplicate-correction" }));
    }

    private static HiringRecommendationResponse HiringCard(WorkforcePlan plan) =>
        new(plan.Id, plan.WorkstreamId, plan.Title, plan.Objective, plan.Status.ToString(), null, [], plan.CreatedAt, plan.UpdatedAt)
        { SourceResourceChangeRequestId = plan.SourceResourceChangeRequestId, RoleKey = plan.RoleKey, RemainingHeadcount = plan.Headcount - plan.FulfilledHeadcount };

    [Fact]
    public async Task SharedTeamApprovalMayCarryCapacityBetweenProjectsWithoutLosingCorrectionAuthority()
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.ManagerInstallationId);
        manager.ApplicationUserId = Guid.NewGuid();
        var resources = new ResourceChangeService(db, new TestAuditEventWriter());
        var first = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "initial"));
        var old = await db.ResourceChangeRequests.SingleAsync(); old.Status = ResourceChangeRequestStatus.Superseded;
        old.TeamId = Guid.NewGuid(); old.WorkstreamId = Guid.NewGuid();
        var current = new ResourceChangeRequestRecord { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SupersedesRequestId = old.Id,
            TeamId = old.TeamId, WorkstreamId = Guid.NewGuid(), Status = ResourceChangeRequestStatus.Approved,
            ContextRevision = first.ContextRevision, RequesterOrganizationUserId = f.RequesterId, RequesterInstallationId = f.RequesterInstallationId,
            ManagerOrganizationUserId = manager.Id, Roles = [new() { Id = Guid.NewGuid(), RoleKey = first.Roles[0].RoleKey, Headcount = 1, IsDesired = true }] };
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SourceResourceChangeRequestId = old.Id,
            RoleKey = first.Roles[0].RoleKey, Headcount = 1, Status = ProposalStatus.Pending };
        db.AddRange(current, plan); await db.SaveChangesAsync();
        var service = new StaffingCorrectionService(db);
        var action = Assert.Single(await service.WithActionsAsync(f.OrganizationId, manager.ApplicationUserId.Value, [HiringCard(plan)], default));
        Assert.Equal(current.Id, action.CorrectionSourceRequestId);
        var correction = await service.RequestAsync(f.OrganizationId, plan.Id, manager.ApplicationUserId.Value,
            new(first.ContextRevision, "Correct shared-team capacity", "shared-correction") { ExpectedSourceRequestId = current.Id });
        Assert.Equal(current.TeamId, correction.TeamId); Assert.Equal(current.WorkstreamId, correction.WorkstreamId);
        Assert.Equal(ProposalStatus.Cancelled, plan.Status);
    }
}
