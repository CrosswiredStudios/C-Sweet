using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class AgentWorkspaceBroker
{
    public async Task<AgentBrokerWorkspaceLockResult> LocksAsync(AgentBrokerWorkspaceLockRequest request, CancellationToken ct = default)
    {
        if (request.Operation is not ("list" or "create" or "unlock") || string.IsNullOrWhiteSpace(request.Workspace.IdempotencyKey) || request.Workspace.IdempotencyKey.Length > 160)
            throw new ArgumentException("Invalid workspace lock operation.");
        var workspace = await AuthorizeWorkspaceOperationAsync(request.Workspace, ct);
        if (workspace.Repository!.Connection!.Provider != SourceControlProvider.InternalGit)
            throw new InvalidOperationException("Agent-owned file locks currently require an internal repository.");
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(u => u.OrganizationId == workspace.OrganizationId &&
            u.AgentInstallationId == workspace.AgentInstallationId && u.IsActive, ct);
        var result = await gitHost.InternalLocksAsync(new(workspace.OrganizationId, workspace.RepositoryId, actor.Id, actor.DisplayName,
            request.Operation, request.Path, request.Id, false, false, request.Cursor), ct);
        var ownedReplay = request.Operation == "create" && result.StatusCode == 409 && result.Locks.Count == 1 && result.Locks[0].OwnerId == actor.Id;
        var status = result.StatusCode is 200 or 201 || ownedReplay || request.Operation == "unlock" && result.StatusCode == 404
            ? request.Operation switch { "list" => "Listed", "create" => "Locked", _ => "Unlocked" }
            : result.StatusCode == 403 ? "Denied" : "Conflict";
        return new(status, result.Locks.Select(l => new AgentBrokerWorkspaceFileLock(l.Id, l.Path, l.OwnerName, l.OwnerId == actor.Id, l.LockedAt)).ToArray(),
            result.NextCursor, ownedReplay ? null : result.Message);
    }
}
