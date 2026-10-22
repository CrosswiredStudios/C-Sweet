using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.Setup;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Infrastructure.Setup;

public sealed partial class ExecutionFleetService
{
    private const string UpdateInterrupted = "office-update-interrupted";

    private async Task<bool> CanStartAutomaticUpdateAsync(Guid officeId, CreateLocalOfficeSetupSessionRequest request, CancellationToken ct)
    {
        if (request.Repair || !Version.TryParse(request.TargetVersion, out var target) ||
            request.TargetVersion.Length > 64 || request.TargetVersion.Split('.').Length != 3) return false;
        var office = await dbContext.ExecutionNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == officeId, ct);
        var now = timeProvider.GetUtcNow();
        if (office is null || office.OperatingSystem != "windows" || office.ApprovedAt is null || office.RevokedAt is not null ||
            office.Status is not (ExecutionNodeStatus.Ready or ExecutionNodeStatus.Draining) ||
            office.CertificateExpiresAt <= now || office.CertificateExpiresAt is null || office.LastHeartbeatAt < now.AddSeconds(-30) ||
            office.LastHeartbeatAt is null || !Version.TryParse(office.NodeVersion, out var installed) || target <= installed) return false;
        var local = DevelopmentLauncherConfigured && MachineMatchesHost(office);
        // Older remote configurators can only reinstall their own payload. Never stop jobs for that path.
        if (!local && installed < new Version(0, 12, 0)) return false;
        if (request.UseLocalSource && (!local || request.TargetVersion != LocalOfficeDevelopmentSource.ReadVersion(FleetPolicy))) return false;
        return !await dbContext.ExecutionWorkloadAssignments.AnyAsync(x => x.ExecutionNodeId == officeId &&
            x.StopEvidenceVersion != 1 && (x.Status == ExecutionAssignmentStatus.Assigned || x.Status == ExecutionAssignmentStatus.Starting ||
                x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping), ct);
    }

    private static bool MachineMatchesHost(ExecutionNode office) => LocalOfficeDevelopmentSource.MatchesHost(
        office.MachineName, office.OperatingSystem, office.Architecture);

    public async Task<OfficeUpdatePreparationResponse> BeginOfficeUpdatePreparationAsync(BeginOfficeUpdatePreparationRequest request, CancellationToken cancellationToken = default)
    {
        if (!ValidUpdateMachine(request.MachineName, request.OperatingSystem, request.Architecture) ||
            string.IsNullOrWhiteSpace(request.HandoffSecret) || request.HandoffSecret.Length > 256)
            return new(false, "invalid_request", "failed");
        var now = timeProvider.GetUtcNow();
        var hash = Hash(request.HandoffSecret);
        var session = await dbContext.LocalOfficeSetupSessions.SingleOrDefaultAsync(x => x.HandoffSecretHash == hash, cancellationToken);
        if (session is not { AutomaticUpdate: true, Status: LocalOfficeSetupSessionStatus.Created, PreparationStartedAt: null } ||
            session.ExpiresAt <= now || session.CancellationRequestedAt is not null ||
            !MachineMatches(session, request.MachineName, request.OperatingSystem, request.Architecture))
            return new(false, "invalid_handoff", "failed");
        var receipt = Base64Url(RandomNumberGenerator.GetBytes(32));
        // Consume the browser handoff now. A bounded, machine-bound receipt keeps a slow download
        // independent of the five-minute handoff and never authorizes installation on its own.
        session.HandoffSecretHash = Hash(Base64Url(RandomNumberGenerator.GetBytes(32)));
        session.PreparationReceiptHash = Hash(receipt);
        session.PreparationStartedAt = now;
        session.ExpiresAt = now.AddHours(2);
        UpdateChanged(session, "preparing");
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { dbContext.ChangeTracker.Clear(); return new(false, "handoff_already_used", "failed"); }
        return new(true, null, "preparing", session.TargetVersion, receipt, ControlPlaneUrl: session.ControlPlaneOrigin);
    }

    public async Task<OfficeUpdatePreparationResponse> AdvanceOfficeUpdatePreparationAsync(OfficeUpdatePreparationRequest request, CancellationToken cancellationToken = default)
    {
        if (!ValidUpdateMachine(request.MachineName, request.OperatingSystem, request.Architecture) ||
            string.IsNullOrWhiteSpace(request.Receipt) || request.Receipt.Length > 256 ||
            request.ErrorCode is not (null or "package_preparation_failed" or "preparation_interrupted"))
            return new(false, "invalid_request", "failed");
        var hash = Hash(request.Receipt);
        var session = await dbContext.LocalOfficeSetupSessions.Include(x => x.UpdateJobs).SingleOrDefaultAsync(x =>
            x.Id == request.SessionId && x.PreparationReceiptHash == hash && x.AutomaticUpdate, cancellationToken);
        if (session is null || session.ExpiresAt <= timeProvider.GetUtcNow() ||
            !MachineMatches(session, request.MachineName, request.OperatingSystem, request.Architecture))
            return new(false, "invalid_receipt", "failed");
        if (session.CancellationRequestedAt is not null || session.Status is LocalOfficeSetupSessionStatus.Revoked or LocalOfficeSetupSessionStatus.Failed)
            return new(false, session.ErrorCode ?? "update_cancelled", session.UpdatePhase ?? "failed");
        if (request.ErrorCode is not null)
        {
            session.Status = LocalOfficeSetupSessionStatus.Failed;
            session.ErrorCode = request.ErrorCode;
            session.ErrorMessage = "The update package could not be prepared. The current Office installation has been kept.";
            UpdateChanged(session, "failed");
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(false, request.ErrorCode, "failed");
        }
        if (request.PackagePrepared && session.PreparationReadyAt is null && session.Status == LocalOfficeSetupSessionStatus.Created)
        {
            await StopOfficeUpdateJobsAsync(session, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        if (session.PreparationReadyAt is null) return new(true, null, "preparing", session.TargetVersion);
        if (session.Status != LocalOfficeSetupSessionStatus.Created) return new(false, "update_already_started", session.UpdatePhase ?? "installing");
        if (!await CanUpgradeOfficeAsync(session.UpgradeOfficeId, cancellationToken))
            return new(true, null, "stopping", session.TargetVersion);
        var handoff = Base64Url(RandomNumberGenerator.GetBytes(32));
        session.HandoffSecretHash = Hash(handoff);
        UpdateChanged(session, "installing");
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, null, "installing", session.TargetVersion, LaunchUri: LocalSetupLaunchUri(session, handoff));
    }

    private async Task StopOfficeUpdateJobsAsync(LocalOfficeSetupSession session, CancellationToken ct)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(ct) : null;
        var now = timeProvider.GetUtcNow();
        var office = await dbContext.ExecutionNodes.SingleAsync(x => x.Id == session.UpgradeOfficeId, ct);
        if (dbContext.Database.IsNpgsql())
        {
            // Assignment placement owns the same pool row lock. Freeze new placements
            // before reading/snapshotting the work to be stopped, then commit the drain atomically.
            _ = await dbContext.ExecutionPools.FromSqlInterpolated($"SELECT * FROM \"ExecutionPools\" WHERE \"Id\" = {office.ExecutionPoolId} FOR UPDATE").SingleAsync(ct);
            await dbContext.Entry(office).ReloadAsync(ct);
            _ = await dbContext.ExecutionWorkloadAssignments.FromSqlInterpolated($"SELECT * FROM \"ExecutionWorkloadAssignments\" WHERE \"ExecutionNodeId\" = {office.Id} FOR UPDATE").ToListAsync(ct);
        }
        if (office.ApprovedAt is null || office.RevokedAt is not null || office.Status is not (ExecutionNodeStatus.Ready or ExecutionNodeStatus.Draining))
            throw new InvalidOperationException("The Office is no longer authorized for an update.");
        if (office.DrainingAt is null) office.WorkPauseReason = OfficeWorkPauseReason.Maintenance;
        office.DrainingAt ??= now;
        office.Status = ExecutionNodeStatus.Draining;
        office.UpdatedAt = now;
        session.PreparationReadyAt = now;
        session.StopStartedAt ??= now;
        var assignments = await dbContext.ExecutionWorkloadAssignments.Include(x => x.AgentRuntimeInstance)!.ThenInclude(x => x!.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .Where(x => x.ExecutionNodeId == office.Id &&
                (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned ||
                 x.Status == ExecutionAssignmentStatus.Starting || x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping ||
                 dbContext.ExecutionAssignmentAttempts.Any(a => a.AssignmentId == x.Id && a.ExecutionNodeId == office.Id && a.StoppedAt == null)))
            .ToListAsync(ct);
        foreach (var assignment in assignments)
        {
            var interrupted = assignment.IsActive;
            if (!session.UpdateJobs.Any(x => x.AssignmentId == assignment.Id)) session.UpdateJobs.Add(new()
            {
                SessionId = session.Id, AssignmentId = assignment.Id, Kind = assignment.WorkloadKind,
                Name = assignment.AgentRuntimeInstance?.AgentInstallation?.PackageVersion?.AgentName ?? assignment.WorkloadKind.ToString(),
                AgentBuildJobId = assignment.AgentBuildJobId, AgentRuntimeInstanceId = assignment.AgentRuntimeInstanceId,
                DeliveryBuildId = assignment.DeliveryBuildId,
                // Keep a stop that was already requested before this maintenance
                // operation; only recover work this update actually interrupted.
                NeedsRecovery = interrupted && assignment.Status != ExecutionAssignmentStatus.Stopping &&
                    assignment.AgentRuntimeInstance?.Status != AgentRuntimeStatus.Stopping
            });
            if (!interrupted) continue;
            // Fencing ends execution authority, while the independent stop journal supplies
            // physical teardown evidence. Do not treat this state change as proof of shutdown.
            assignment.Status = ExecutionAssignmentStatus.Cancelled;
            assignment.CompletedAt = now;
            assignment.LeaseExpiresAt = null;
            assignment.FencingEpoch++;
            assignment.FailureCode = UpdateInterrupted;
            assignment.SanitizedFailure = "Stopped for a planned Office update. Eligible work will restart after verified teardown.";
            if (assignment.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild && assignment.DeliveryBuildId is { } deliveryId &&
                await dbContext.DeliveryBuilds.SingleOrDefaultAsync(x => x.Id == deliveryId, ct) is { } delivery &&
                delivery.Status is "Queued" or "Claimed" or "Running")
            {
                delivery.LeaseExpiresAt = session.ExpiresAt;
                delivery.FailureCode = UpdateInterrupted;
                delivery.MaintenanceInterruptionCount++;
                delivery.UpdatedAt = now;
            }
            if (assignment.WorkloadKind == ExecutionWorkloadKind.Runtime && assignment.AgentRuntimeInstance is { } runtime &&
                AgentRuntimeInstance.IsActive(runtime.Status) && runtime.Status != AgentRuntimeStatus.Stopping)
            {
                runtime.TransitionTo(AgentRuntimeStatus.Stopping, now, UpdateInterrupted);
                dbContext.AgentRuntimeEvents.Add(new() { Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id,
                    Status = AgentRuntimeStatus.Stopping, Reason = UpdateInterrupted, OccurredAt = now });
                foreach (var mcp in await dbContext.McpAgentSessions.Where(x => x.RuntimeInstanceId == runtime.Id && x.RevokedAt == null).ToListAsync(ct))
                { mcp.RevokedAt = now; mcp.RevocationReason = "Planned Office update; runtime authority is fenced."; }
                // Retain live durable work until teardown and update recovery have settled it.
                // The revoked runtime cannot renew this lease or deliver a late completion.
                foreach (var attempt in await dbContext.AgentWorkAttempts.Where(x => x.RuntimeInstanceId == runtime.Id && x.FinishedAt == null).ToListAsync(ct))
                    attempt.LeaseExpiresAt = session.ExpiresAt;
            }
        }
        UpdateChanged(session, "stopping");
        await dbContext.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<LocalOfficeSetupActionResponse> CancelOfficeUpdateAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.LocalOfficeSetupSessions.Include(x => x.UpdateJobs).SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.CreatedByUserId == userId && x.AutomaticUpdate, cancellationToken);
        if (session is null) return await LocalFailureAsync("session_not_found", "The update was not found.", cancellationToken);
        if (session.CancellationRequestedAt is not null) return new(true, null, "Update cancellation is in progress.", Map(session, null, null), await GetOnboardingStatusAsync(cancellationToken));
        if (session.Status != LocalOfficeSetupSessionStatus.Created || session.UpdatePhase is not ("preparing" or "stopping"))
            return await LocalFailureAsync("update_cannot_cancel", "Installation has started. Keep the computer on while the update finishes.", cancellationToken);
        session.CancellationRequestedAt = timeProvider.GetUtcNow();
        session.HandoffSecretHash = Hash(Base64Url(RandomNumberGenerator.GetBytes(32)));
        UpdateChanged(session, "cancelling");
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, null, "Cancelling the update and restoring the previous work setting.", Map(session, null, null), await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task ReconcileOfficeUpdatesAsync(CancellationToken cancellationToken = default)
    {
        await ExpireLocalSetupSessionsAsync(timeProvider.GetUtcNow(), cancellationToken);
        // Bounded control-plane recovery also discovers operations after missed notifications
        // or a worker restart. Browsers never own advancement or installation authority.
        var ids = await dbContext.LocalOfficeSetupSessions.AsNoTracking().Where(x => x.AutomaticUpdate && x.UpdatePhase != "superseded" &&
            (x.WorkRestoredAt == null || x.UpdateJobs.Any(j => j.NeedsRecovery && j.RecoveredAt == null)) &&
            x.Status != LocalOfficeSetupSessionStatus.RecoveryRequired)
            .OrderBy(x => x.LastUpdateCheckAt ?? DateTimeOffset.MinValue).ThenBy(x => x.Id).Select(x => x.Id).Take(16).ToArrayAsync(cancellationToken);
        foreach (var id in ids)
        {
            try { await ReconcileOfficeUpdateAsync(id, cancellationToken); }
            catch (DbUpdateConcurrencyException) { dbContext.ChangeTracker.Clear(); }
        }
    }

    private async Task ReconcileOfficeUpdateAsync(Guid id, CancellationToken ct)
    {
        var session = await dbContext.LocalOfficeSetupSessions.Include(x => x.UpdateJobs).SingleAsync(x => x.Id == id, ct);
        var now = timeProvider.GetUtcNow();
        session.LastUpdateCheckAt = now;
        await dbContext.SaveChangesAsync(ct);
        if (session.Status == LocalOfficeSetupSessionStatus.Created && session.AdministratorApprovalRequestedAt is null &&
            session.CancellationRequestedAt is null && session.ExpiresAt > now)
        {
            var handoff = Base64Url(RandomNumberGenerator.GetBytes(32));
            session.HandoffSecretHash = Hash(handoff);
            await dbContext.SaveChangesAsync(ct);
            var launch = await LaunchLocalSetupSessionAsync(id, session.CreatedByUserId, new(LocalSetupLaunchUri(session, handoff)), ct);
            if (!launch.Succeeded)
            {
                session.Status = LocalOfficeSetupSessionStatus.Failed;
                session.ErrorCode = launch.ErrorCode;
                session.ErrorMessage = launch.Message;
                UpdateChanged(session, "failed");
                await dbContext.SaveChangesAsync(ct);
            }
            return;
        }
        var office = await dbContext.ExecutionNodes.Include(x => x.Providers).SingleOrDefaultAsync(x => x.Id == session.UpgradeOfficeId, ct);
        if (office is null || office.RevokedAt is not null || office.ApprovedAt is null) return;
        if (session.Status == LocalOfficeSetupSessionStatus.Connected && session.CompletedAt is { } installedAt &&
            office.NodeVersion == session.TargetVersion && office.LastHeartbeatAt > installedAt && office.LastHeartbeatAt >= now.AddSeconds(-30) &&
            office.CertificateExpiresAt > now && office.Status is ExecutionNodeStatus.Ready or ExecutionNodeStatus.Draining &&
            IdentityAndProvidersQualify(office, now) && CapacityQualifies(office, ExecutionWorkloadKind.Builder) &&
            CapacityQualifies(office, ExecutionWorkloadKind.Runtime))
        {
            session.Status = LocalOfficeSetupSessionStatus.Ready;
            UpdateChanged(session, "resuming");
        }
        var cancelled = session.CancellationRequestedAt is not null && session.Status == LocalOfficeSetupSessionStatus.Created;
        var preparationFailed = session.Status is LocalOfficeSetupSessionStatus.Failed or LocalOfficeSetupSessionStatus.Expired && session.StopStartedAt is null;
        if (cancelled && session.StopStartedAt is not null &&
            (!await UpdateJobsStoppedAsync(session, ct) || !OfficeHealthyForUpdateRecovery(office, now))) return;
        // An explicitly reviewed connection repair can recover a failed update's
        // work policy without pretending that its selected version was installed.
        var repaired = false;
        if (session.WorkRestoredAt is null && session.StopStartedAt is not null &&
            session.Status is LocalOfficeSetupSessionStatus.Failed or LocalOfficeSetupSessionStatus.Expired &&
            OfficeHealthyForUpdateRecovery(office, now) && await UpdateJobsStoppedAsync(session, ct))
        {
            repaired = await dbContext.LocalOfficeSetupSessions.AnyAsync(x => !x.AutomaticUpdate &&
                x.UpgradeOfficeId == office.Id && x.RecoveryAction == "repair" && x.CreatedAt >= session.CreatedAt &&
                x.CompletedAt >= session.UpdatedAt && office.LastHeartbeatAt > x.CompletedAt &&
                (x.Status == LocalOfficeSetupSessionStatus.Connected || x.Status == LocalOfficeSetupSessionStatus.Ready), ct);
        }
        if ((session.Status == LocalOfficeSetupSessionStatus.Ready || cancelled || preparationFailed || repaired) && session.WorkRestoredAt is null)
        {
            if (session.StopStartedAt is not null && session.ResumeAfterUpdate && office.WorkPauseReason != OfficeWorkPauseReason.User)
            {
                office.DrainingAt = null;
                office.WorkPauseReason = null;
                office.Status = office.LastHeartbeatAt >= now.AddSeconds(-30) ? ExecutionNodeStatus.Ready : ExecutionNodeStatus.Offline;
                office.UpdatedAt = now;
            }
            session.WorkRestoredAt = now;
            if (cancelled) session.Status = LocalOfficeSetupSessionStatus.Revoked;
            session.PreparationReceiptHash = null;
            UpdateChanged(session, cancelled ? "cancelled" : preparationFailed || repaired ? "failed" : "complete");
            await dbContext.SaveChangesAsync(ct);
        }
        if (session.WorkRestoredAt is not null && office.DrainingAt is null &&
            office.Status == ExecutionNodeStatus.Ready) await RecoverOfficeUpdateJobsAsync(session, ct);
        var display = Map(session, null, null);
        if (display.State == "failed" && session.Status is LocalOfficeSetupSessionStatus.Created or LocalOfficeSetupSessionStatus.Redeemed)
        {
            session.Status = LocalOfficeSetupSessionStatus.Failed;
            session.ErrorCode = display.ErrorCode;
            session.ErrorMessage = display.ErrorMessage;
            UpdateChanged(session, "failed");
            await dbContext.SaveChangesAsync(ct);
        }
    }

    private bool OfficeHealthyForUpdateRecovery(ExecutionNode office, DateTimeOffset now) =>
        office.LastHeartbeatAt >= now.AddSeconds(-30) && office.CertificateExpiresAt > now &&
        office.Status is ExecutionNodeStatus.Ready or ExecutionNodeStatus.Draining &&
        IdentityAndProvidersQualify(office, now) && CapacityQualifies(office, ExecutionWorkloadKind.Builder) &&
        CapacityQualifies(office, ExecutionWorkloadKind.Runtime);

    private async Task<bool> UpdateJobsStoppedAsync(LocalOfficeSetupSession session, CancellationToken ct) =>
        !await dbContext.ExecutionAssignmentAttempts.AnyAsync(x => x.ExecutionNodeId == session.UpgradeOfficeId && x.StoppedAt == null, ct) &&
        !await dbContext.ExecutionWorkloadAssignments.AnyAsync(x => x.ExecutionNodeId == session.UpgradeOfficeId &&
            (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned || x.Status == ExecutionAssignmentStatus.Starting ||
             x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping), ct);

    private async Task RecoverOfficeUpdateJobsAsync(LocalOfficeSetupSession session, CancellationToken ct)
    {
        if (services is null) return;
        foreach (var job in session.UpdateJobs.Where(x => x.NeedsRecovery && x.RecoveredAt is null))
        {
            var assignment = await dbContext.ExecutionWorkloadAssignments.SingleOrDefaultAsync(x => x.Id == job.AssignmentId, ct);
            if (assignment is null || assignment.FailureCode != UpdateInterrupted ||
                await dbContext.ExecutionAssignmentAttempts.AnyAsync(x => x.AssignmentId == job.AssignmentId && x.StoppedAt == null, ct)) continue;
            try
            {
                if (job.Kind == ExecutionWorkloadKind.Runtime && job.AgentRuntimeInstanceId is { } runtimeId)
                {
                    var runtime = await dbContext.AgentRuntimeInstances.SingleOrDefaultAsync(x => x.Id == runtimeId, ct);
                    if (runtime is null) { job.RecoveredAt = timeProvider.GetUtcNow(); continue; }
                    if (!AgentRuntimeInstance.IsTerminal(runtime.Status)) continue;
                    await RecoverPlannedWorkAsync(runtime.Id, session.StopStartedAt ?? session.CreatedAt, ct);
                    var installation = await dbContext.AgentInstallations.AsNoTracking().Include(x => x.Schedule).SingleAsync(x => x.Id == runtime.AgentInstallationId, ct);
                    if (installation.IsEnabled && installation.RevisionStatus == PluginRevisionStatus.Active)
                    {
                        if (installation.Schedule is { IsEnabled: true, ActivationMode: ActivationMode.Scheduled } &&
                            !await dbContext.AgentRuntimeInstances.AnyAsync(x => x.AgentInstallationId == installation.Id &&
                                (x.Status == AgentRuntimeStatus.Queued || x.Status == AgentRuntimeStatus.Starting || x.Status == AgentRuntimeStatus.Running ||
                                 x.Status == AgentRuntimeStatus.WaitingForMcpSession || x.Status == AgentRuntimeStatus.Stopping), ct))
                            dbContext.AgentRuntimeInstances.Add(new() { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installation.Id,
                                QueuedAt = timeProvider.GetUtcNow(), IsInteractive = runtime.IsInteractive });
                        else
                            await services.GetRequiredService<IAgentRuntimeManager>().EnsureRuntimeQueuedAsync(runtime.AgentInstallationId,
                                "Office update completed; recover interrupted durable work.", runtime.IsInteractive, ct);
                    }
                }
                else if (job.Kind == ExecutionWorkloadKind.Builder && job.AgentBuildJobId is { } buildId)
                {
                    var build = await dbContext.AgentBuildJobs.SingleOrDefaultAsync(x => x.Id == buildId, ct);
                    if (build is null) { job.RecoveredAt = timeProvider.GetUtcNow(); continue; }
                    if (build.Status is AgentBuildStatus.Cloning or AgentBuildStatus.Building or AgentBuildStatus.Queued) continue;
                    await services.GetRequiredService<IAgentBuildService>().QueueAsync(build.PackageVersionId, ct);
                }
                else if (job.Kind == ExecutionWorkloadKind.ToolchainBuild && job.DeliveryBuildId is { } deliveryId)
                {
                    var build = await dbContext.DeliveryBuilds.SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
                    if (build is not null && build.FailureCode == UpdateInterrupted && build.Status is not ("CancelRequested" or "Cancelled" or "Succeeded"))
                    {
                        build.Status = "Queued"; build.ClaimId = null; build.LeaseExpiresAt = null;
                        build.ExecutionNodeId = null; build.LastHeartbeatAt = null; build.Revision++; build.UpdatedAt = timeProvider.GetUtcNow();
                        assignment.ExecutionNodeId = null; assignment.Status = ExecutionAssignmentStatus.Pending;
                        assignment.AssignedAt = null; assignment.StartedAt = null; assignment.CompletedAt = null;
                        assignment.LeaseExpiresAt = null; assignment.ProviderInstanceId = null;
                        assignment.ResultArtifactLocator = null; assignment.ResultArtifactDigest = null; assignment.ResultArtifactSignature = null;
                        assignment.ResultArtifactFormatVersion = null; assignment.ResultArtifactOperatingSystem = null; assignment.ResultArtifactArchitecture = null;
                        assignment.QueuedAt = timeProvider.GetUtcNow();
                    }
                }
                job.RecoveredAt = timeProvider.GetUtcNow();
                job.RecoveryError = null;
                await dbContext.SaveChangesAsync(ct);
            }
            catch (AgentInstallationException)
            {
                job.RecoveryError = "The agent is not ready to restart. Check its configuration in Installed agents.";
                await dbContext.SaveChangesAsync(ct);
            }
        }
    }

    private void UpdateChanged(LocalOfficeSetupSession session, string phase)
    {
        session.UpdatePhase = phase;
        session.UpdatedAt = timeProvider.GetUtcNow();
        dbContext.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(), RecipientApplicationUserId = session.CreatedByUserId,
            EventType = AppRealtimeEvents.OfficeUpdateChanged, Subject = $"offices/{session.UpgradeOfficeId:D}/update",
            DataJson = JsonSerializer.Serialize(new { sessionId = session.Id, officeId = session.UpgradeOfficeId, phase }),
            OccurredAt = session.UpdatedAt, NextAttemptAt = session.UpdatedAt
        });
        dbContext.QueueAudit(new("office.update." + phase, EntityType: nameof(LocalOfficeSetupSession),
            EntityId: session.Id, Summary: $"Office update {phase} for {session.UpgradeOfficeId:D}; target {session.TargetVersion}.", UseAmbientOrganization: false));
    }

    private async Task RecoverPlannedWorkAsync(Guid runtimeId, DateTimeOffset stoppedAt, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var attempts = await dbContext.AgentWorkAttempts.Include(x => x.AgentWorkItem)
            .Where(x => x.RuntimeInstanceId == runtimeId && x.FinishedAt == null).ToListAsync(ct);
        foreach (var attempt in attempts)
        {
            attempt.FinishedAt = now;
            attempt.Error = UpdateInterrupted;
            if (attempt.AgentWorkItem is not { Status: AgentWorkStatus.Leased } work) continue;
            work.Status = AgentWorkStatus.Pending;
            work.AvailableAt = now;
            work.DeadlineAt += now - stoppedAt;
            work.MaintenanceInterruptionCount++;
            work.LastError = "Paused for an Office update; restarting from the last saved progress.";
        }
    }

    private LocalOfficeSetupSessionResponse MapAutomaticUpdate(LocalOfficeSetupSession session, string? launchMethod)
    {
        var phase = session.UpdatePhase ?? "preparing";
        var progress = ReadWindowsSetupProgress(session.Id, timeProvider.GetUtcNow());
        var failed = session.CancellationRequestedAt is null && session.Status != LocalOfficeSetupSessionStatus.Ready &&
            (session.Status is LocalOfficeSetupSessionStatus.Failed or LocalOfficeSetupSessionStatus.Expired || progress?.State == "failed");
        var state = failed ? "failed" : phase == "cancelled" ? "revoked" : session.WorkRestoredAt is not null &&
            session.Status == LocalOfficeSetupSessionStatus.Ready ? "ready" : phase;
        if (session.Status == LocalOfficeSetupSessionStatus.Redeemed) phase = "installing";
        if (session.Status == LocalOfficeSetupSessionStatus.Connected) phase = "reconnecting";
        if (failed) phase = "failed";
        var jobs = dbContext.Set<OfficeUpdateJob>().AsNoTracking().Where(x => x.SessionId == session.Id).ToArray()
            .Select(j => new OfficeUpdateJobResponse(j.AssignmentId, j.Name, j.Kind.ToString(), j.RecoveryError is not null ? "Needs attention" : j.RecoveredAt is not null ? "Queued for restart" :
                dbContext.ExecutionAssignmentAttempts.Any(a => a.AssignmentId == j.AssignmentId && a.StoppedAt == null) ? "Stopping" : "Stopped")).ToArray();
        var remaining = jobs.Count(x => x.State == "Stopping");
        var workPaused = dbContext.ExecutionNodes.AsNoTracking().Any(x => x.Id == session.UpgradeOfficeId && x.DrainingAt != null);
        var (title, message) = phase switch
        {
            "preparing" => (session.AdministratorApprovalRequestedAt is null ? "Starting your update" : "Preparing the update",
                "Downloading and verifying the update before interrupting work. Allow the Windows prompt if one appears."),
            "stopping" => ($"Stopping {jobs.Length} job{(jobs.Length == 1 ? "" : "s")}",
                $"New work is paused. {jobs.Length - remaining} of {jobs.Length} jobs have confirmed they stopped. Installation continues automatically."),
            "installing" => ("Installing the update", "Running jobs are stopped. We’re replacing Office software and restarting its services."),
            "reconnecting" => ("Reconnecting your Office", "Installation finished. Waiting for a fresh connection, the selected version, and healthy capacity."),
            "resuming" => (session.ResumeAfterUpdate ? "Resuming work" : "Keeping work paused", "The updated Office has been verified. Restoring your previous work setting."),
            "complete" => ("Office updated", workPaused ? "The new version and connection are verified. Work is currently paused." :
                "The new version and connection are verified. Your Office is ready for work."),
            "cancelling" => ("Cancelling the update", "Confirming outstanding jobs have stopped and the Office is healthy before restoring the previous work setting."),
            "cancelled" => ("Update cancelled", "The current Office installation was kept and the previous work setting restored."),
            _ => ("Update needs attention", session.StopStartedAt is null ? "The update could not be prepared. Current work was not interrupted." :
                session.WorkRestoredAt is not null ? "The previous work setting was restored after connection recovery. The selected update version has not been confirmed." :
                "The update could not finish. Work remains paused until the Office is safe to use.")
        };
        return new(session.Id, state, phase, title, message, 0, session.ExpiresAt, session.AllocatableCpuCount,
            session.AllocatableMemoryMb, session.AllocatableDiskMb, session.MaximumConcurrentWorkloads, null, null,
            session.ErrorCode ?? (failed ? progress?.ErrorCode : null), session.ErrorMessage ?? (failed ? progress?.ErrorMessage : null),
            LaunchMethod: launchMethod, AdministratorApprovalRequestedAt: session.AdministratorApprovalRequestedAt,
            RecoveryAction: "upgrade", UpgradeOfficeId: session.UpgradeOfficeId, AutomaticUpdate: true, TargetVersion: session.TargetVersion,
            ResumeAfterUpdate: session.ResumeAfterUpdate, StopStartedAt: session.StopStartedAt, UpdateJobs: jobs,
            CanCancelUpdate: session.Status == LocalOfficeSetupSessionStatus.Created && session.CancellationRequestedAt is null &&
                phase is "preparing" or "stopping");
    }

    private static bool ValidUpdateMachine(string machine, string os, string architecture) =>
        !string.IsNullOrWhiteSpace(machine) && machine.Length <= 255 && os == "windows" && architecture is "x64" or "arm64";
}
