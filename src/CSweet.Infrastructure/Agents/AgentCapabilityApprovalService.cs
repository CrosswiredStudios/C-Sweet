using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Agents;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Agents;

public sealed class AgentCapabilityApprovalService(CSweetDbContext db, TimeProvider clock) : IAgentCapabilityApprovalService
{
    private static CapabilityApprovalResponse Map(AgentCapabilityApproval row) => new(row.Id, row.AgentInstallationId,
        row.Capability, row.PayloadJson, row.Risk, row.Status, row.CreatedAt, row.UpdatedAt);

    public async Task<CapabilityAdmission> AdmitAsync(Guid organization, Guid installation, string capability,
        byte[] payload, long grantRevision, CapabilityRiskClass risk, CancellationToken token = default)
    {
        if (payload.Length > 1024 * 1024) throw new InvalidOperationException("The request exceeds the approval payload limit.");
        await using var transaction = await GovernanceTransaction.OpenAsync(db, installation.ToString("D"), token);
        var agent = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installation &&
            (organization == Guid.Empty ? x.Scope == PluginInstallationScope.User && x.OwnerApplicationUserId != null : x.BusinessId == organization.ToString("D")) && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token)
            ?? throw new UnauthorizedAccessException("The agent installation is not active in this business.");
        var currentGrant = await db.AgentInstallationGrants.AsNoTracking().Where(x => x.AgentInstallationId == installation).Select(x => (long?)x.GrantRevision).SingleOrDefaultAsync(token);
        if (currentGrant != grantRevision) return new(Guid.Empty, "GrantChanged");
        if (agent.Posture is not (null or "Manual")) return new(Guid.Empty, "PostureChanged");
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{installation:N}:{grantRevision}:{agent.GovernanceRevision}:{capability}:{digest}")));
        var row = await db.AgentCapabilityApprovals.SingleOrDefaultAsync(x => x.OperationKey == key, token);
        if (row is not null) await RefreshAsync(row, token);
        if (row is null)
        {
            row = new() { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
                OperationKey = key, Capability = capability, PayloadJson = Encoding.UTF8.GetString(payload), Risk = risk.ToString(),
                GrantRevision = grantRevision, PostureRevision = agent.GovernanceRevision,
                CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            db.AgentCapabilityApprovals.Add(row);
            await GovernanceEvents.EnqueueAsync(db, organization, installation, row.Id, row.Status, false, token);
            await db.SaveChangesAsync(token);
        }
        if (row.Status == "Approved")
        {
            // Claim once before dispatch. An unconfirmed execution is never automatically repeated.
            // Authority changes supersede Approved rows without this installation lock, so the claim is
            // conditional on the row still being Approved at the revision read here.
            if (!await ClaimAsync(row, token))
            {
                await RefreshAsync(row, token);
                await transaction.CommitAsync(token);
                return new(row.Id, row.Status, row.ResultsJson);
            }
            await GovernanceEvents.EnqueueAsync(db, organization, installation, row.Id, row.Status, false, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(row.Id, "Execute");
        }
        await transaction.CommitAsync(token);
        return new(row.Id, row.Status, row.ResultsJson);
    }

    public async Task DeferAsync(Guid approval, CancellationToken token = default)
    {
        await using var transaction = await GovernanceTransaction.OpenAsync(db, approval.ToString("D"), token);
        var row = await db.AgentCapabilityApprovals.SingleAsync(x => x.Id == approval, token);
        await RefreshAsync(row, token);
        if (row.Status != "Executing") throw new InvalidOperationException("The approval is no longer current.");
        row.Status = "Approved"; row.Revision++; row.UpdatedAt = clock.GetUtcNow();
        // The agent may retry once the window reopens; tell it and the inbox in the same transaction.
        await GovernanceEvents.EnqueueAsync(db, row.OrganizationId, row.AgentInstallationId, row.Id, row.Status, true, token);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }

    public async Task CompleteAsync(Guid approval, string resultsJson, bool succeeded, CancellationToken token = default)
    {
        await using var transaction = await GovernanceTransaction.OpenAsync(db, approval.ToString("D"), token);
        var row = await db.AgentCapabilityApprovals.SingleAsync(x => x.Id == approval, token);
        await RefreshAsync(row, token);
        if (row.Status != "Executing") throw new InvalidOperationException("The approval execution is no longer current.");
        row.ResultsJson = resultsJson; row.Status = succeeded ? "Succeeded" : "Failed"; row.Revision++; row.UpdatedAt = clock.GetUtcNow();
        await GovernanceEvents.EnqueueAsync(db, row.OrganizationId, row.AgentInstallationId, row.Id, row.Status, false, token);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<CapabilityApprovalResponse>> ListAsync(Guid organization, Guid user, bool administrator,
        CancellationToken token = default)
    {
        var query = db.AgentCapabilityApprovals.AsNoTracking().Where(x => x.OrganizationId == organization);
        if (!administrator)
        {
            if (organization == Guid.Empty)
                query = query.Where(x => db.AgentInstallations.Any(a => a.Id == x.AgentInstallationId && a.Scope == PluginInstallationScope.User && a.OwnerApplicationUserId == user));
            else
            {
                var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                    x.ApplicationUserId == user && x.IsActive && x.ArchivedAt == null && x.EmployeeType == CSweet.Domain.Core.EmployeeType.Human, token);
                if (actor is null || actor.PermissionLevel < CSweet.Domain.Core.OrganizationPermissionLevel.Manager) return [];
                var installations = db.CoreOrganizationUsers.Where(x => x.OrganizationId == organization && x.IsActive && x.ArchivedAt == null && x.AgentInstallationId != null &&
                    (actor.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Owner || x.ReportsToOrganizationUserId == actor.Id)).Select(x => x.AgentInstallationId!.Value);
                query = query.Where(x => installations.Contains(x.AgentInstallationId));
            }
        }
        var rows = await query.OrderBy(x => x.Status == "Pending" ? 0 : 1).ThenByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
        // The predicate above is the authorization filter, so paging after a second in-memory filter
        // no longer returns fewer rows than the caller is entitled to see.
        return rows.Select(Map).ToArray();
    }

    public async Task<CapabilityApprovalResponse> AbandonAsync(Guid organization, Guid approval, Guid user, bool administrator,
        CancellationToken token = default)
    {
        await using var transaction = await GovernanceTransaction.OpenAsync(db, approval.ToString("D"), token);
        var row = await db.AgentCapabilityApprovals.SingleOrDefaultAsync(x => x.Id == approval && x.OrganizationId == organization, token)
            ?? throw new KeyNotFoundException("Approval not found.");
        await RefreshAsync(row, token);
        if (!await AgentGovernanceAccess.CanManageAsync(db, user, row.AgentInstallationId, administrator, token))
            throw new UnauthorizedAccessException("You may not abandon this agent's approval.");
        if (row.Status != "Executing")
            throw new InvalidOperationException("Only an unconfirmed execution can be abandoned.");
        row.Status = "Failed";
        row.ResultsJson = null;
        row.DecidedByApplicationUserId = user;
        row.UpdatedAt = clock.GetUtcNow(); row.Revision++;
        await GovernanceEvents.EnqueueAsync(db, organization, row.AgentInstallationId, row.Id, row.Status, true, token, user);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return Map(row);
    }

    public async Task<CapabilityApprovalResponse> DecideAsync(Guid organization, Guid approval, Guid user, bool administrator,
        bool approve, CancellationToken token = default)
    {
        await using var transaction = await GovernanceTransaction.OpenAsync(db, approval.ToString("D"), token);
        var row = await db.AgentCapabilityApprovals.SingleOrDefaultAsync(x => x.Id == approval && x.OrganizationId == organization, token)
            ?? throw new KeyNotFoundException("Approval not found.");
        await RefreshAsync(row, token);
        if (!await AgentGovernanceAccess.CanManageAsync(db, user, row.AgentInstallationId, administrator, token))
            throw new UnauthorizedAccessException("You may not decide this agent's approval.");
        if (row.Status != "Pending") throw new InvalidOperationException("This approval has already been decided.");
        var agent = await db.AgentInstallations.AsNoTracking().SingleAsync(x => x.Id == row.AgentInstallationId, token);
        if (agent.GovernanceRevision != row.PostureRevision) throw new InvalidOperationException("The agent posture changed. Request a fresh approval.");
        var grantRevision = await db.AgentInstallationGrants.AsNoTracking().Where(x => x.AgentInstallationId == row.AgentInstallationId).Select(x => (long?)x.GrantRevision).SingleOrDefaultAsync(token);
        if (!agent.IsEnabled || agent.RevisionStatus != PluginRevisionStatus.Active || grantRevision != row.GrantRevision)
            throw new InvalidOperationException("The active grant changed. Request a fresh approval.");
        row.Status = approve ? "Approved" : "Denied"; row.DecidedByApplicationUserId = user;
        row.UpdatedAt = clock.GetUtcNow(); row.Revision++;
        await GovernanceEvents.EnqueueAsync(db, organization, row.AgentInstallationId, row.Id, row.Status, true, token, user);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return Map(row);
    }
    private async Task<bool> ClaimAsync(AgentCapabilityApproval row, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        if (db.Database.IsRelational())
        {
            var revision = row.Revision;
            var claimed = await db.AgentCapabilityApprovals.Where(x => x.Id == row.Id && x.Status == "Approved" && x.Revision == revision)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, "Executing").SetProperty(a => a.Revision, a => a.Revision + 1)
                    .SetProperty(a => a.UpdatedAt, now), token);
            if (claimed != 1) return false;
            await db.Entry(row).ReloadAsync(token);
            return true;
        }
        // Non-relational stores have no concurrent writers, but the claim still has to be conditional:
        // an already-claimed or superseded row must never be claimed twice.
        if (row.Status != "Approved") return false;
        row.Status = "Executing"; row.Revision++; row.UpdatedAt = now;
        return true;
    }

    private async Task RefreshAsync(AgentCapabilityApproval row, CancellationToken token)
    {
        // A broker scope can survive while the API decides the request in another process.
        await db.Entry(row).ReloadAsync(token);
        if (db.Entry(row).State == EntityState.Detached) throw new KeyNotFoundException("Approval no longer exists.");
    }

}
