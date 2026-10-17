using CSweet.Agent.SDK;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed partial class GitWorkspaceCapabilityHandler
{
    private Task<GitWorkspaceSyncResult> SyncWorkspaceAsync(Guid organization, Guid installation, GitWorkspaceSyncRequest input, CancellationToken ct, Guid runtimeId, long grantRevision) =>
        input.Direction == "push" ? CSweet.Infrastructure.Setup.RuntimeCheckpointGate.ExecuteAsync(db, installation, organization.ToString(), runtimeId, grantRevision,
            () => SyncWorkspaceCoreAsync(organization, installation, input, ct, runtimeId), ct)
            : SyncWorkspaceCoreAsync(organization, installation, input, ct, runtimeId);

    private async Task<GitWorkspaceSyncResult> SyncWorkspaceCoreAsync(Guid organization, Guid installation, GitWorkspaceSyncRequest input, CancellationToken ct, Guid runtimeId)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (input.ResumeSummary is { } summary && (summary.SourceFingerprint is not { Length: 64 } || !summary.SourceFingerprint.All(Uri.IsHexDigit) ||
            summary.Validations is null || summary.Validations.Count > 16 || summary.Validations.Any(x => x is null || x.Command is not { Length: > 0 and <= 512 })))
            throw new ArgumentException("A bounded source-bound resumption summary is required.");
        if (input.CommandClass is not null && input.CommandClass is not ("build" or "test" or "restore" or "analysis") ||
            input.InputFingerprint is not null && (input.InputFingerprint.Length != 64 || !input.InputFingerprint.All(Uri.IsHexDigit)) ||
            (input.CommandClass is null) != (input.InputFingerprint is null)) throw new ArgumentException("Invalid resource profile inputs.");
        if (input.AssignmentRevision < 1) throw new ArgumentException("A current assignment revision is required.");
        if (input.Direction is not ("pull" or "push") || input.Direction == "pull" && input.Archive is not null ||
            input.Direction == "push" && (input.Archive is not { Length: > 0 } ||
                                           input.Archive.Length > _transferLimits.MaximumArchiveBytes))
            throw new ArgumentException("A bounded pull or push snapshot transfer is required.");
        // The sync declaration does not expand repository rights: reads require preparation
        // authority and writes require publication authority for this exact assignment.
        var qa = await (from review in db.TaskDeliveryReviews.AsNoTracking()
            join workspace in db.SourceControlWorkspaces.AsNoTracking() on review.TaskId equals workspace.WorkItemId
            where review.OrganizationId == organization && review.QaInstallationId == installation && review.Status == "Testing" &&
                workspace.Id == input.WorkspaceId && workspace.AgentInstallationId == installation && workspace.OrganizationId == organization
            select review.Id).AnyAsync(ct);
        var context = await RequireWorkspaceContextAsync(organization, installation, input.WorkspaceId, input.AssignmentRevision,
            qa ? GitWorkspaceCapabilities.Sync : input.Direction == "pull" ? GitWorkspaceCapabilities.Prepare : GitWorkspaceCapabilities.Publish, ct);
        var result = await gitHost.SyncAsync(Operation(context, input.IdempotencyKey), input.Direction, input.Archive, ct);
        if (input.Direction != "push")
        {
            var repositoryId = await db.SourceControlWorkspaces.Where(x => x.Id == input.WorkspaceId).Select(x => x.RepositoryId).SingleAsync(ct);
            var organizationKey = organization.ToString();
            var profiles = await db.WorkloadResourceProfiles.AsNoTracking().Where(x => x.OrganizationId == organizationKey && x.RepositoryId == repositoryId)
                .OrderByDescending(x => x.UpdatedAt).Take(64).Select(x => new WorkloadResourceHint(x.CommandClass, x.InputFingerprint, x.MemoryMb)).ToArrayAsync(ct);
            var retainedSummary = await db.SourceControlWorkspaces.Where(x => x.Id == input.WorkspaceId).Select(x => x.CheckpointSummaryJson).SingleAsync(ct);
            return result with { ResourceProfiles = profiles, ResumeSummary = retainedSummary is null ? null : System.Text.Json.JsonSerializer.Deserialize<WorkspaceResumeSummary>(retainedSummary, JsonOptions) };
        }
        var workspaceRecord = await db.SourceControlWorkspaces.SingleAsync(x => x.Id == input.WorkspaceId, ct);
        workspaceRecord.CheckpointDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input.Archive!));
        workspaceRecord.CheckpointRuntimeId = runtimeId;
        workspaceRecord.CheckpointAt = DateTimeOffset.UtcNow;
        if (input.ResumeSummary is not null) workspaceRecord.CheckpointSummaryJson = System.Text.Json.JsonSerializer.Serialize(input.ResumeSummary, JsonOptions);
        if (input.CommandClass is not null)
        {
            workspaceRecord.CheckpointCommandClass = input.CommandClass;
            workspaceRecord.CheckpointInputFingerprint = input.InputFingerprint!.ToLowerInvariant();
            var active = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).Where(x => x.RuntimeInstanceId == runtimeId && x.FinishedAt == null).Take(2).ToArrayAsync(ct);
            var work = active.Length == 1 ? active[0].AgentWorkItem : null;
            var root = work is null ? null : await CSweet.Infrastructure.WorkManagement.AgentTicketFeedback.ResolveAsync(db, work, ct);
            if (work is not null && root is not null &&
                (await CSweet.Infrastructure.WorkManagement.AgentTicketFeedback.ExecutionTicketsAsync(db, root, ct)).Any(x => x.Id == workspaceRecord.WorkItemId))
            {
                var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    $"{work.OrganizationId}:{workspaceRecord.RepositoryId:N}:{input.CommandClass}:{input.InputFingerprint.ToLowerInvariant()}")));
                work.ResourceProfileKey = key;
                var profile = await db.WorkloadResourceProfiles.FindAsync([key], ct);
                if (profile is null)
                {
                    var allocated = await db.AgentRuntimeInstances.Where(x => x.Id == runtimeId).Select(x => x.AllocatedMemoryMb).SingleAsync(ct);
                    db.WorkloadResourceProfiles.Add(new() { Key = key, OrganizationId = work.OrganizationId, RepositoryId = workspaceRecord.RepositoryId,
                        CommandClass = input.CommandClass, InputFingerprint = input.InputFingerprint.ToLowerInvariant(), MemoryMb = allocated, UpdatedAt = DateTimeOffset.UtcNow });
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return result with { Checkpoint = new(input.WorkspaceId, input.AssignmentRevision, workspaceRecord.CheckpointDigest) };
    }
}
