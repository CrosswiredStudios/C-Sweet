using CSweet.Agent.SDK;

namespace CSweet.AgentHost.Broker;

public sealed partial class GitWorkspaceCapabilityHandler
{
    private async Task<GitWorkspaceSyncResult> SyncWorkspaceAsync(Guid organization, Guid installation, GitWorkspaceSyncRequest input, CancellationToken ct)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (input.AssignmentRevision < 1) throw new ArgumentException("A current assignment revision is required.");
        if (input.Direction is not ("pull" or "push") || input.Direction == "pull" && input.Archive is not null ||
            input.Direction == "push" && (input.Archive is not { Length: > 0 } ||
                                           input.Archive.Length > _transferLimits.MaximumArchiveBytes))
            throw new ArgumentException("A bounded pull or push snapshot transfer is required.");
        // The sync declaration does not expand repository rights: reads require preparation
        // authority and writes require publication authority for this exact assignment.
        var context = await RequireWorkspaceContextAsync(organization, installation, input.WorkspaceId, input.AssignmentRevision,
            input.Direction == "pull" ? GitWorkspaceCapabilities.Prepare : GitWorkspaceCapabilities.Publish, ct);
        return await gitHost.SyncAsync(Operation(context, input.IdempotencyKey), input.Direction, input.Archive, ct);
    }
}
