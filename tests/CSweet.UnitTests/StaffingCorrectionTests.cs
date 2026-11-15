using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ResourceChangeServiceTests
{
    [Fact]
    public async Task OwnerCorrectionStopsOnlyDisputedUnfilledHireAndReplacementUsesNormalApproval()
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var resources = new ResourceChangeService(db, new TestAuditEventWriter());
        var baseline = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "correction-baseline"));
        var source = await db.ResourceChangeRequests.SingleAsync(x => x.Id == baseline.Id);
        source.Status = ResourceChangeRequestStatus.Approved;
        var owner = User(f.OrganizationId, "Owner", EmployeeType.Human);
        owner.ApplicationUserId = Guid.NewGuid(); owner.PermissionLevel = OrganizationPermissionLevel.Owner;
        db.CoreOrganizationUsers.Add(owner);
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, RequestingInstallationId = f.ManagerInstallationId,
            SourceResourceChangeRequestId = baseline.Id, RoleKey = baseline.Roles[0].RoleKey, Title = "Incorrect slot", Headcount = 1, Status = ProposalStatus.Pending };
        var fulfilled = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, RequestingInstallationId = f.ManagerInstallationId,
            SourceResourceChangeRequestId = baseline.Id, RoleKey = baseline.Roles[1].RoleKey, Title = "Existing teammate", Headcount = 1,
            FulfilledHeadcount = 1, Status = ProposalStatus.Approved };
        var workflow = new StaffingActionProposal { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, WorkforcePlanId = plan.Id, Status = ProposalStatus.Approved };
        var operation = new AgentHireOperation { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, WorkflowId = workflow.Id, Status = AgentHireOperationStatus.Building };
        db.AddRange(plan, fulfilled, workflow, operation); await db.SaveChangesAsync();
        var staleDraft = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId,
            Proposal(f, "older-pending-proposal") with { SupersedesRequestId = baseline.Id, ChatTurnId = Guid.Empty,
                Roles = [baseline.Roles[0] with { Purpose = "Older proposed scope" }, baseline.Roles[1]] });
        var service = new StaffingCorrectionService(db);
        var command = new RequestStaffingCorrection(baseline.ContextRevision, "Replace the misclassified slot with technical planning capacity.", "owner-correction");
        var first = await service.RequestAsync(f.OrganizationId, plan.Id, owner.ApplicationUserId.Value, command);
        db.ChangeTracker.Clear();
        var duplicate = await new StaffingCorrectionService(db).RequestAsync(f.OrganizationId, plan.Id, owner.ApplicationUserId.Value, command);
        Assert.Equal(first.Id, duplicate.Id); Assert.Single(await db.StaffingCorrections.ToListAsync());
        Assert.Equal(ResourceChangeRequestStatus.Approved, (await db.ResourceChangeRequests.SingleAsync(x => x.Id == baseline.Id)).Status);
        Assert.Equal(ProposalStatus.Cancelled, (await db.WorkforcePlans.SingleAsync(x => x.Id == plan.Id)).Status);
        Assert.Equal(ProposalStatus.Approved, (await db.WorkforcePlans.SingleAsync(x => x.Id == fulfilled.Id)).Status);
        Assert.Equal(AgentHireOperationStatus.Cancelled, (await db.AgentHireOperations.SingleAsync()).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.DecideForInstallationAsync(f.OrganizationId, f.ManagerInstallationId,
            new(staleDraft.Id, "Approve", "Stale approval", "stale-approval")));
        Assert.Contains(await db.AgentPlatformEventOutbox.ToListAsync(), x => x.TargetInstallationId == f.RequesterInstallationId && x.DataJson.Contains("CorrectionRequested"));
        var discovery = await resources.ReadForInstallationAsync(f.OrganizationId, f.RequesterInstallationId, new());
        Assert.Equal(first.Id, Assert.Single(discovery.Corrections).Id);
        var replacementRole = baseline.Roles[0] with { RoleKey = "technical-planning", Title = "Technical planning", Purpose = "Resolve feasibility" };
        var replacement = Proposal(f, "corrected-proposal") with { CorrectionId = first.Id, SupersedesRequestId = baseline.Id,
            Roles = [replacementRole, baseline.Roles[1]], ChatTurnId = Guid.Empty,
            Evidence = [new("staffing-correction", $"{first.Id:N}:{first.Revision}", first.Reason)] };
        await Assert.ThrowsAsync<ArgumentException>(() => resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId,
            replacement with { Evidence = [new("staffing-correction", $"{first.Id:N}:{first.Revision}", "Weaker direction")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId,
            replacement with { Roles = [replacementRole] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, replacement with { CorrectionId = null }));
        var revised = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, replacement);
        Assert.Equal("Pending", revised.Status); Assert.Equal(first.Id, revised.CorrectionId);
        Assert.Contains(revised.Deltas, x => x.ChangeKind == "Remove" && x.Role.RoleKey == plan.RoleKey);
        await resources.DecideForInstallationAsync(f.OrganizationId, f.ManagerInstallationId, new(revised.Id, "Approve", "Exact corrected package approved", "approve-correction"));
        Assert.Equal("Completed", (await db.StaffingCorrections.SingleAsync()).Status);
        Assert.Equal(ResourceChangeRequestStatus.Superseded, (await db.ResourceChangeRequests.SingleAsync(x => x.Id == baseline.Id)).Status);
        Assert.Empty((await resources.ReadForInstallationAsync(f.OrganizationId, f.RequesterInstallationId, new())).Corrections);
    }

    [Fact]
    public async Task RemovingTheStaffingAuthorCancelsOutstandingCorrectionWithoutRewritingHistory()
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var service = new ResourceChangeService(db, new TestAuditEventWriter());
        var source = await service.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "source"));
        (await db.ResourceChangeRequests.SingleAsync()).Status = ResourceChangeRequestStatus.Approved;
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SourceResourceChangeRequestId = source.Id,
            RequestingInstallationId = f.RequesterInstallationId, Status = ProposalStatus.Cancelled };
        var correction = new StaffingCorrection { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SourceRequestId = source.Id,
            RecommendationId = plan.Id, Reason = "Correct the slot", RoleKey = "planning", IdempotencyKey = "correction" };
        db.AddRange(plan, correction); await db.SaveChangesAsync();
        await EmployeeActionExpiration.ExpireAsync(db, f.OrganizationId, f.RequesterId, f.RequesterInstallationId, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        Assert.Equal("Cancelled", correction.Status);
        Assert.Equal(ResourceChangeRequestStatus.Approved, (await db.ResourceChangeRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task CorrectionRejectsWrongActorStaleSourceAndFulfilledWork()
    {
        await using var db = CreateDb(); var f = SeedManagerConversation(db); await db.SaveChangesAsync();
        var resources = new ResourceChangeService(db, new TestAuditEventWriter());
        var source = await resources.ProposeAsync(f.OrganizationId, f.RequesterInstallationId, Proposal(f, "source"));
        (await db.ResourceChangeRequests.SingleAsync()).Status = ResourceChangeRequestStatus.Approved;
        var outsider = User(f.OrganizationId, "Contributor", EmployeeType.Human); outsider.ApplicationUserId = Guid.NewGuid(); outsider.PermissionLevel = OrganizationPermissionLevel.Contributor;
        var manager = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.ManagerInstallationId); manager.ApplicationUserId = Guid.NewGuid();
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, SourceResourceChangeRequestId = source.Id,
            RoleKey = source.Roles[0].RoleKey, Headcount = 1, Status = ProposalStatus.Pending };
        db.AddRange(plan, outsider); await db.SaveChangesAsync();
        var service = new StaffingCorrectionService(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestAsync(f.OrganizationId, plan.Id, outsider.ApplicationUserId.Value, new(3, "Fix", "denied")));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RequestAsync(f.OrganizationId, plan.Id, manager.ApplicationUserId.Value, new(99, "Fix", "stale")));
        plan.FulfilledHeadcount = 1; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(f.OrganizationId, plan.Id, manager.ApplicationUserId.Value, new(3, "Fix", "fulfilled")));
        Assert.Empty(await db.StaffingCorrections.ToListAsync());
    }
}
