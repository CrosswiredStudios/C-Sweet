using CSweet.Domain.Core;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    // Repair only an unambiguous, already-approved Chief hire. Never copy the departed
    // installation's policy or select an arbitrary employee as a new authority holder.
    internal async Task RestoreHiredChiefAsync(Guid organizationId, CancellationToken token)
    {
        if (businessOnboarding is null) return;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organizationId, token);
        if (await db.LeadershipAssignments.AnyAsync(x => x.OrganizationId == organizationId &&
                x.PositionKey == LeadershipPositionKeys.ChiefOfStaff && x.EndsAt == null &&
                db.CoreOrganizationUsers.Any(employee => employee.Id == x.OrganizationUserId &&
                    employee.OrganizationId == organizationId && employee.IsActive && employee.ArchivedAt == null), token)) return;
        var candidates = await db.CoreOrganizationUsers.Where(employee => employee.OrganizationId == organizationId &&
            employee.IsActive && employee.ArchivedAt == null && employee.EmployeeType == EmployeeType.Agent &&
            employee.AgentInstallationId != null && employee.AgentInstallation!.IsEnabled &&
            employee.Role != null && employee.Role.Name == "Chief of Staff" &&
            db.StaffingActionProposals.Any(workflow => workflow.OrganizationId == organizationId &&
                workflow.Status == ProposalStatus.Approved && workflow.ResultOrganizationUserId == employee.Id))
            .Select(x => x.Id).Take(2).ToListAsync(token);
        if (candidates.Count != 1) return;
        var result = await businessOnboarding.AssignExistingChiefAsync(organizationId, candidates[0], token);
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        var installationId = await db.CoreOrganizationUsers.Where(x => x.Id == candidates[0])
            .Select(x => x.AgentInstallationId!.Value).SingleAsync(token);
        // Commit the wake hint with the restored authority; existing backlog reads remain authoritative.
        await new AgentAttentionInvalidationService(db, TimeProvider.System).InvalidateAsync(
            [installationId], "leadership.chief-restored", candidates[0], token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }
}
