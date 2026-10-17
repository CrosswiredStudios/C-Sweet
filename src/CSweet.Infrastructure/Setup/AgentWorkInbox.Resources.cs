using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentWorkInbox
{
    // Called only for a guest-broker-owned cgroup OOM receipt, never for a lease expiry,
    // exit 137, model claim or arbitrary log text. The reconciler still stops the old VM.
    public async Task RecoverResourceFailureAsync(Guid runtimeId, CancellationToken token)
    {
        var runtime = await db.AgentRuntimeInstances.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleAsync(x => x.Id == runtimeId, token);
        if (runtime.ResourceFailureCode != "workload-memory-limit-exceeded") return;
        var gate = ClaimLocks.GetOrAdd(runtime.AgentInstallationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (db.Database.IsNpgsql())
            {
                var key = $"agent-work-claim:{runtime.AgentInstallation!.BusinessId}:{runtime.AgentInstallationId:D}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", token);
            }
            var attempts = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).Where(x => x.RuntimeInstanceId == runtimeId && x.FinishedAt == null).Take(17).ToArrayAsync(token);
            var now = timeProvider.GetUtcNow();
            foreach (var attempt in attempts)
            {
                var item = attempt.AgentWorkItem!;
                var workspace = await db.SourceControlWorkspaces.AsNoTracking().Where(x => x.CheckpointRuntimeId == runtimeId &&
                        x.AgentInstallationId == runtime.AgentInstallationId && x.CheckpointAt >= attempt.ClaimedAt && x.CheckpointInputFingerprint != null)
                    .OrderByDescending(x => x.CheckpointAt).FirstOrDefaultAsync(token);
                if (attempts.Length == 1 && workspace is not null && item.DeadlineAt > now)
                {
                    var request = new WorkloadResourceRequest(runtime.AllocatedMemoryMb + 1,
                        new(workspace.Id, workspace.AssignmentRevision, workspace.CheckpointDigest!),
                        workspace.CheckpointCommandClass!, workspace.CheckpointInputFingerprint!, "command-memory-limit");
                    var completion = AgentWorkResult.ResourceHandoff(request);
                    try
                    {
                        var session = new McpAgentSession { RuntimeInstanceId = runtimeId, AgentInstallationId = runtime.AgentInstallationId,
                            OrganizationId = item.OrganizationId, TickId = runtime.TickId, GrantRevision = runtime.AgentInstallation!.Grant!.GrantRevision };
                        var value = new AgentWorkCompletion(false, completion.Value, completion.Error, completion.FailureCode, true);
                        await StageResourceHandoffAsync(session, item, attempt, value,
                            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))), now, token);
                        continue;
                    }
                    catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
                    { /* Policy, stale assignment or exhausted budget requires diagnosis. */ }
                }
                const string failure = "agent-failure:v1;code=runtime.resources.exhausted;retryable=false";
                attempt.FinishedAt = now; attempt.Error = failure;
                item.Status = AgentWorkStatus.DeadLetter; item.CompletedAt = now; item.LastError = failure;
                item.ResourceRecoveryState = "DiagnosisRequired";
                await RecordTicketFailureAsync(item, attempt.Attempt, failure, now, token);
            }
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        }
        finally { gate.Release(); }
    }

    private async Task<WorkloadResources?> ReadResourcesAsync(AgentWorkItem item, Guid runtimeId, CancellationToken token)
    {
        var runtime = await db.AgentRuntimeInstances.AsNoTracking().SingleAsync(x => x.Id == runtimeId, token);
        var settings = await db.AgentRuntimeGlobalSettings.AsNoTracking().FirstOrDefaultAsync(token);
        var toolchain = await db.ExecutionWorkloadAssignments.AsNoTracking().Where(x => x.AgentRuntimeInstanceId == runtimeId)
            .OrderByDescending(x => x.QueuedAt).Select(x => x.GuestImageDigest).FirstOrDefaultAsync(token);
        return runtime.AllocatedMemoryMb > 0 && settings is not null
            ? new(runtime.AllocatedMemoryMb, settings.CodingMaximumMemoryMb, item.ResourceReplacementCount,
                item.ResourceRecoveryState ?? "Running", item.ResourceProfileKey, item.ResourceResumeWorkItemId, toolchain, settings.CodingMaximumReplacements) : null;
    }

    private async Task StageResourceHandoffAsync(McpAgentSession session, AgentWorkItem item, AgentWorkAttempt attempt,
        AgentWorkCompletion completion, string completionHash, DateTimeOffset now, CancellationToken token)
    {
        if (completion.Succeeded || completion.Value is not { ValueKind: JsonValueKind.Object } value)
            throw new ArgumentException("A typed checkpointed resource request is required.");
        var request = value.Deserialize<WorkloadResourceRequest>(JsonOptions) ?? throw new ArgumentException("Resource request is empty.");
        if (request.Checkpoint is null || request.CommandClass is not ("build" or "test" or "restore" or "analysis") ||
            request.InputFingerprint is not { Length: 64 } || !request.InputFingerprint.All(Uri.IsHexDigit) ||
            request.Reason is not ("project-estimate" or "command-memory-limit"))
            throw new ArgumentException("Resource request contains invalid evidence.");
        var runtime = await db.AgentRuntimeInstances.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleAsync(x => x.Id == session.RuntimeInstanceId, token);
        var confirmedMemoryFailure = request.Reason == "command-memory-limit" && runtime.ResourceFailureCode == "workload-memory-limit-exceeded";
        if (request.Reason == "command-memory-limit" && !confirmedMemoryFailure)
            throw new UnauthorizedAccessException("Only guest-supervisor evidence can confirm a memory failure.");
        if ((!confirmedMemoryFailure && runtime.Status != AgentRuntimeStatus.Running) || runtime.AgentInstallation is not { IsEnabled: true, RevisionStatus: PluginRevisionStatus.Active, Grant: not null } installation ||
            installation.Grant.GrantRevision != session.GrantRevision || installation.BusinessId != item.OrganizationId ||
            AgentRuntimeGuestSelection.ReadRequest(runtime.AgentInstallation.PackageVersion!.ManifestJson).EnvironmentProfile != "software-development-polyglot-v1")
            throw new UnauthorizedAccessException("Only the active coding runtime may request this handoff.");
        if (await db.AgentWorkAttempts.AnyAsync(x => x.RuntimeInstanceId == runtime.Id && x.FinishedAt == null && x.Id != attempt.Id, token))
            throw new InvalidOperationException("Resource replacement requires all other callbacks to quiesce.");
        var checkpoint = request.Checkpoint;
        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == checkpoint.WorkspaceId &&
            x.OrganizationId.ToString() == item.OrganizationId && x.AgentInstallationId == item.AgentInstallationId &&
            x.AssignmentRevision == checkpoint.AssignmentRevision && x.CheckpointRuntimeId == runtime.Id &&
            x.CheckpointDigest == checkpoint.Digest && x.CheckpointAt >= attempt.ClaimedAt, token)
            ?? throw new UnauthorizedAccessException("A completed source checkpoint from this attempt is required.");
        var ticket = await CSweet.Infrastructure.WorkManagement.AgentTicketFeedback.ResolveAsync(db, item, token);
        var target = ticket is null ? null : (await CSweet.Infrastructure.WorkManagement.AgentTicketFeedback.ExecutionTicketsAsync(db, ticket, token))
            .SingleOrDefault(x => x.Id == workspace.WorkItemId);
        if (target is null || target.AssignmentRevision != workspace.AssignmentRevision ||
            target.AssignedAgentInstallationId != item.AgentInstallationId)
            throw new UnauthorizedAccessException("The resource request is not bound to the current assignment.");
        var settings = await db.AgentRuntimeGlobalSettings.SingleAsync(token);
        var next = WorkloadResourcePolicy.Grow(runtime.AllocatedMemoryMb, request.MemoryMb,
            settings.CodingMaximumMemoryMb, item.ResourceReplacementCount, settings.CodingMaximumReplacements, confirmedMemoryFailure);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{item.OrganizationId}:{workspace.RepositoryId:N}:{request.CommandClass}:{request.InputFingerprint.ToLowerInvariant()}")));
        var profile = await db.WorkloadResourceProfiles.SingleOrDefaultAsync(x => x.Key == key, token);
        if (profile is null)
        {
            profile = new() { Key = key, OrganizationId = item.OrganizationId, RepositoryId = workspace.RepositoryId,
                CommandClass = request.CommandClass, InputFingerprint = request.InputFingerprint.ToLowerInvariant() };
            db.WorkloadResourceProfiles.Add(profile);
        }
        profile.MemoryMb = Math.Max(next, profile.MemoryMb); profile.Revision++; profile.UpdatedAt = now;
        if (request.Reason == "command-memory-limit") profile.MemoryFailures++;
        item.RequestedMemoryMb = next; item.ResourceReplacementCount++; item.ResourceProfileKey = key;
        item.ResourceCheckpointDigest = checkpoint.Digest; item.ResourceRecoveryState = "WaitingForReplacement";
        item.ResourceResumeWorkItemId = ticket!.Id;
        item.Status = AgentWorkStatus.Pending; item.AvailableAt = now; item.LastError = null;
        attempt.FinishedAt = now; attempt.LastConfirmedAt = now; attempt.CompletionHash = completionHash;
        attempt.Error = "runtime.resources.handoff";
        const string reason = "runtime.resources.handoff: checkpoint retained; stop the old VM before resuming the same operation.";
        if (AgentRuntimeInstance.IsActive(runtime.Status)) runtime.TransitionTo(AgentRuntimeStatus.Stopping, now, reason);
        db.AgentRuntimeEvents.Add(new() { Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id,
            Status = runtime.Status, Reason = reason, OccurredAt = now });
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(item.OrganizationId),
            TargetInstallationId = item.AgentInstallationId, EventType = WorkloadResourceEvents.Changed,
            IdempotencyKey = $"runtime-resources:{item.Id:N}:{item.ResourceReplacementCount}",
            DataJson = JsonSerializer.Serialize(new WorkloadResourcesChanged(item.Id, item.ResourceReplacementCount), JsonOptions),
            OccurredAt = now, NextAttemptAt = now });
        // The durable queue and stopping event commit together. Reconciliation rediscovers both
        // after restart; no agent polling or event payload can authorize a second execution.
        db.QueueAudit(new("agent-runtime.resources.handoff", EntityType: nameof(AgentWorkItem), EntityId: item.Id,
            Summary: $"Checkpointed operation requests {next} MiB; replacement {item.ResourceReplacementCount}.", OccurredAt: now));
    }
}
