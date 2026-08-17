using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeManager(
    CSweetDbContext dbContext,
    IAgentWorkloadRunner workloads,
    IGuestImageRegistry guestImages,
    IAuditEventWriter auditWriter,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<AgentRuntimeManager> logger,
    IAgentRuntimeEligibilityService eligibility) : IPluginRuntimeManager
{
    private const int MaximumAlwaysOnStartupAttempts = 3;
    private static readonly AgentRuntimeStatus[] WorkloadActiveStatuses =
    [AgentRuntimeStatus.Starting, AgentRuntimeStatus.WaitingForMcpSession, AgentRuntimeStatus.Running, AgentRuntimeStatus.CompletionReported, AgentRuntimeStatus.Stopping];

    public async Task<bool> EnsureRuntimeQueuedAsync(
        Guid installationId,
        string reason,
        bool interactive = false,
        CancellationToken cancellationToken = default)
    {
        var activeRuntime = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .OrderByDescending(x => x.QueuedAt)
            .FirstOrDefaultAsync(
                x => x.AgentInstallationId == installationId &&
                    (x.Status == AgentRuntimeStatus.Queued || WorkloadActiveStatuses.Contains(x.Status)),
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (activeRuntime is not null)
        {
            if (interactive && activeRuntime.AgentInstallation?.Schedule?.ActivationMode == ActivationMode.OnDemand)
            {
                activeRuntime.IsInteractive = true;
                activeRuntime.LastInteractiveActivityAt = now;
                activeRuntime.IdleDeadlineAt = now.AddSeconds(
                    Math.Max(1, options.Value.InteractiveIdleTimeoutSeconds));
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return false;
        }

        var runtimeEligibility = await EvaluateEligibilityAsync(installationId, cancellationToken);
        if (!runtimeEligibility.IsEligible)
            throw new AgentInstallationException(runtimeEligibility.Reason ?? "The installation is not eligible to run.");

        var activationMode = await dbContext.AgentSchedules
            .Where(x => x.AgentInstallationId == installationId)
            .Select(x => x.ActivationMode)
            .SingleAsync(cancellationToken);
        if (activationMode == ActivationMode.Scheduled)
            return false;
        var instance = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installationId,
            QueuedAt = now,
            IsInteractive = interactive,
            LastInteractiveActivityAt = interactive ? now : null,
            IdleDeadlineAt = interactive && activationMode != ActivationMode.AlwaysOn
                ? now.AddSeconds(Math.Max(1, options.Value.InteractiveIdleTimeoutSeconds))
                : null
        };
        instance.Events.Add(new AgentRuntimeEvent
        {
            Id = Guid.NewGuid(),
            AgentRuntimeInstanceId = instance.Id,
            Status = AgentRuntimeStatus.Queued,
            Reason = reason,
            OccurredAt = now
        });
        dbContext.AgentRuntimeInstances.Add(instance);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "UX_AgentRuntimeInstances_ActiveInstallation"
            })
        {
            foreach (var entry in dbContext.ChangeTracker.Entries()
                         .Where(entry => ReferenceEquals(entry.Entity, instance) || instance.Events.Contains(entry.Entity)))
            {
                entry.State = EntityState.Detached;
            }

            if (interactive)
            {
                var winner = await dbContext.AgentRuntimeInstances
                    .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
                    .OrderByDescending(x => x.QueuedAt)
                    .FirstAsync(
                        x => x.AgentInstallationId == installationId &&
                            (x.Status == AgentRuntimeStatus.Queued || WorkloadActiveStatuses.Contains(x.Status)),
                        cancellationToken);
                if (winner.AgentInstallation?.Schedule?.ActivationMode == ActivationMode.OnDemand)
                {
                    winner.IsInteractive = true;
                    winner.LastInteractiveActivityAt = now;
                    winner.IdleDeadlineAt = now.AddSeconds(
                        Math.Max(1, options.Value.InteractiveIdleTimeoutSeconds));
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            return false;
        }
        await auditWriter.WriteAsync(
            "agent-runtime.interactive.queued",
            nameof(AgentRuntimeInstance),
            instance.Id,
            reason,
            cancellationToken: cancellationToken);
        return true;
    }

    public async Task<bool> RestartRuntimeAsync(
        Guid installationId,
        string reason,
        bool interactive = false,
        CancellationToken cancellationToken = default)
    {
        var runtimeEligibility = await EvaluateEligibilityAsync(installationId, cancellationToken);
        if (!runtimeEligibility.IsEligible)
            throw new AgentInstallationException(runtimeEligibility.Reason ?? "The installation is not eligible to restart.");

        var activeRuntime = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .OrderByDescending(x => x.QueuedAt)
            .FirstOrDefaultAsync(
                x => x.AgentInstallationId == installationId &&
                    (x.Status == AgentRuntimeStatus.Queued || WorkloadActiveStatuses.Contains(x.Status)),
                cancellationToken);
        if (activeRuntime is not null)
        {
            await StopAndFinishAsync(
                activeRuntime,
                AgentRuntimeStatus.Cancelled,
                reason,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        return await EnsureRuntimeQueuedAsync(
            installationId,
            reason,
            interactive,
            cancellationToken);
    }

    public async Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default)
    {
        var installationIds = await dbContext.AgentInstallations
            .AsNoTracking()
            .Where(x => x.IsEnabled &&
                x.Schedule != null &&
                x.Schedule.IsEnabled &&
                x.Schedule.ActivationMode == ActivationMode.AlwaysOn &&
                x.Schedule.AutomaticStartSuppressedAt == null &&
                !x.RuntimeInstances.Any(runtime =>
                    runtime.Status == AgentRuntimeStatus.Queued ||
                    WorkloadActiveStatuses.Contains(runtime.Status)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var installationId in installationIds)
        {
            if (await EnsureRuntimeQueuedAsync(
                    installationId,
                    "Queued by always-on runtime reconciliation.",
                    cancellationToken: cancellationToken))
            {
                queued++;
            }
        }

        return queued;
    }

    public async Task<int> EnsurePendingOnDemandRuntimesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var installationIds = await dbContext.AgentInstallations
            .AsNoTracking()
            .Where(x => x.IsEnabled &&
                x.Schedule != null &&
                x.Schedule.IsEnabled &&
                x.Schedule.ActivationMode == ActivationMode.OnDemand &&
                dbContext.AgentWorkItems.Any(work =>
                    work.AgentInstallationId == x.Id &&
                    work.Status == AgentWorkStatus.Pending &&
                    work.AvailableAt <= now &&
                    work.DeadlineAt > now) &&
                !x.RuntimeInstances.Any(runtime =>
                    runtime.Status == AgentRuntimeStatus.Queued ||
                    WorkloadActiveStatuses.Contains(runtime.Status)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var installationId in installationIds)
        {
            try
            {
                if (await EnsureRuntimeQueuedAsync(
                        installationId,
                        "Queued because durable on-demand work is pending.",
                        interactive: true,
                        cancellationToken))
                {
                    queued++;
                }
            }
            catch (AgentInstallationException exception)
            {
                logger.LogDebug(exception,
                    "On-demand installation {InstallationId} is waiting for runtime capacity.",
                    installationId);
            }
        }

        return queued;
    }

    public async Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dueIds = await dbContext.AgentSchedules.AsNoTracking()
            .Where(x => x.IsEnabled && x.NextTickAt != null && x.NextTickAt <= now)
            .OrderBy(x => x.NextTickAt).Select(x => x.Id)
            .Take(Math.Clamp(options.Value.MaximumScheduleClaimsPerIteration, 1, 100))
            .ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var id in dueIds)
        {
            if (await ClaimAndQueueAsync(id, now, cancellationToken)) processed++;
        }
        return processed;
    }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var changed = 0;
        var now = DateTimeOffset.UtcNow;
        var strandedRefreshes = await dbContext.AgentInstallations
            .Where(x =>
                x.ConfigurationSyncStatus == AgentConfigurationSyncStatus.Refreshing &&
                x.AppliedConfigurationRevision < x.DesiredConfigurationRevision &&
                x.ConfigurationSyncLastAttemptAt != null &&
                x.ConfigurationSyncLastAttemptAt <= now.AddMinutes(-5) &&
                !dbContext.AgentWorkItems.Any(work =>
                    work.AgentInstallationId == x.Id &&
                    work.Kind == AgentWorkKind.ConfigurationUpdate &&
                    (work.Status == AgentWorkStatus.Pending || work.Status == AgentWorkStatus.Leased)))
            .ToListAsync(cancellationToken);
        foreach (var installation in strandedRefreshes)
        {
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.Restarting;
            installation.ConfigurationSyncLastError ??=
                "The configuration refresh ended without an acknowledgment and will be retried on restart.";
        }
        if (strandedRefreshes.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);

        var restartIds = await dbContext.AgentInstallations.AsNoTracking()
            .Where(x => x.ConfigurationSyncStatus == AgentConfigurationSyncStatus.Restarting &&
                x.RuntimeInstances.Any(runtime => runtime.Status == AgentRuntimeStatus.Queued ||
                    WorkloadActiveStatuses.Contains(runtime.Status)))
            .Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var installationId in restartIds)
        {
            var installation = await dbContext.AgentInstallations.SingleAsync(x => x.Id == installationId, cancellationToken);
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart;
            await dbContext.SaveChangesAsync(cancellationToken);
            if (await RestartRuntimeAsync(installationId,
                    "Restarted because the agent requested a configuration restart fallback.",
                    cancellationToken: cancellationToken))
                changed++;
        }
        var stoppedRestartIds = await dbContext.AgentInstallations
            .Where(x => x.ConfigurationSyncStatus == AgentConfigurationSyncStatus.Restarting &&
                !x.RuntimeInstances.Any(runtime => runtime.Status == AgentRuntimeStatus.Queued ||
                    WorkloadActiveStatuses.Contains(runtime.Status)))
            .ToListAsync(cancellationToken);
        foreach (var installation in stoppedRestartIds)
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.PendingNextStart;
        if (stoppedRestartIds.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
        var instances = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)!.ThenInclude(x => x!.BuildJobs)
            .Include(x => x.Events)
            .Where(x => x.Status == AgentRuntimeStatus.Queued || WorkloadActiveStatuses.Contains(x.Status))
            .OrderBy(x => x.QueuedAt).ToListAsync(cancellationToken);

        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtimeEligibility = await EvaluateEligibilityAsync(instance.AgentInstallationId, cancellationToken);
            if (!runtimeEligibility.IsEligible)
            {
                await StopAndFinishAsync(instance, AgentRuntimeStatus.PolicyDenied,
                    runtimeEligibility.Reason ?? "Runtime eligibility was revoked.", now, cancellationToken);
                changed++;
                continue;
            }
            if (instance.Status == AgentRuntimeStatus.Stopping)
            {
                var settings = await SettingsAsync(cancellationToken);
                var stoppingAt = instance.Events
                    .Where(x => x.Status == AgentRuntimeStatus.Stopping)
                    .MaxBy(x => x.OccurredAt)?.OccurredAt ?? instance.StartedAt ?? instance.QueuedAt;
                if (stoppingAt.AddSeconds(settings.WorkloadStopGraceSeconds + 5) <= now)
                {
                    await RecoverInterruptedStopAsync(instance, settings, now, cancellationToken);
                    changed++;
                }
                continue;
            }
            if (instance.Status == AgentRuntimeStatus.Starting)
            {
                var settings = await SettingsAsync(cancellationToken);
                var startingAt = instance.Events
                    .Where(x => x.Status == AgentRuntimeStatus.Starting)
                    .MaxBy(x => x.OccurredAt)?.OccurredAt ?? instance.StartedAt ?? instance.QueuedAt;
                if (startingAt.AddSeconds(settings.WorkloadStartTimeoutSeconds + 5) <= now)
                {
                    await RecoverInterruptedStartAsync(instance, settings, now, cancellationToken);
                    changed++;
                }
                continue;
            }
            if (instance.Status == AgentRuntimeStatus.Queued)
            {
                if (await TryStartAsync(instance, now, cancellationToken)) changed++;
                continue;
            }
            if (instance.Status == AgentRuntimeStatus.CompletionReported)
            {
                await StopAndFinishAsync(instance, AgentRuntimeStatus.Completed, "Agent completion processed.", now, cancellationToken);
                changed++;
                continue;
            }
            if (instance.Status != AgentRuntimeStatus.Queued &&
                (instance.AgentInstallation?.IsEnabled != true || instance.AgentInstallation.Schedule?.IsEnabled != true))
            {
                await StopAndFinishAsync(instance, AgentRuntimeStatus.Cancelled, "Installation or schedule was disabled.", now, cancellationToken);
                changed++;
                continue;
            }
            if (instance.Status == AgentRuntimeStatus.WaitingForMcpSession)
            {
                var settings = await SettingsAsync(cancellationToken);
                var waitingAt = instance.McpSessionWaitingAt ?? instance.Events
                    .Where(x => x.Status == AgentRuntimeStatus.WaitingForMcpSession)
                    .MaxBy(x => x.OccurredAt)?.OccurredAt;
                if (waitingAt?.AddSeconds(settings.McpSessionTimeoutSeconds) <= now)
                {
                    await StopAndFinishAsync(instance, AgentRuntimeStatus.McpSessionTimedOut, "MCP session establishment timed out.", now, cancellationToken);
                    changed++;
                    continue;
                }
            }
            if (instance.Status == AgentRuntimeStatus.Running && instance.RuntimeDeadlineAt <= now)
            {
                await StopAndFinishAsync(instance, AgentRuntimeStatus.RuntimeTimedOut, "Maximum runtime elapsed.", now, cancellationToken);
                changed++;
                continue;
            }
            if (instance.Status == AgentRuntimeStatus.Running &&
                instance.IdleDeadlineAt is { } idleDeadline &&
                idleDeadline <= now &&
                instance.AgentInstallation?.Schedule?.ActivationMode != ActivationMode.AlwaysOn)
            {
                await StopAndFinishAsync(
                    instance,
                    AgentRuntimeStatus.Cancelled,
                    "Interactive runtime idle timeout elapsed.",
                    now,
                    cancellationToken);
                changed++;
                continue;
            }
            if (TryGetHandle(instance) is { } handle && instance.Status is AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Running)
            {
                var status = await workloads.InspectAsync(handle, cancellationToken);
                if (status is null || status.State is IsolationWorkloadState.Stopped or IsolationWorkloadState.Destroyed or IsolationWorkloadState.Failed)
                {
                    var terminal = status?.ExitCode is 0 ? AgentRuntimeStatus.ExitedWithoutCompletion : AgentRuntimeStatus.Failed;
                    await StopAndFinishAsync(instance, terminal, status?.SanitizedError ?? "Isolated workload exited without a completion event.", now, cancellationToken);
                    changed++;
                }
            }
        }
        return changed;
    }

    private async Task RecoverInterruptedStopAsync(
        AgentRuntimeInstance instance,
        AgentRuntimeGlobalSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (TryGetHandle(instance) is { } handle)
        {
            try
            {
                var status = await workloads.InspectAsync(handle, cancellationToken);
                if (status is not null)
                {
                    await workloads.DestroyAsync(handle, cancellationToken);
                }
                instance.ProviderInstanceId = null;
                instance.IsolationProviderId = null;
            }
            catch (AgentWorkloadException exception)
            {
                logger.LogWarning(exception, "Interrupted stop cleanup failed for runtime {RuntimeInstanceId}.", instance.Id);
            }
        }

        const string recoveryReason = "Recovered a runtime interrupted while stopping; a fresh attempt can now start.";
        Transition(instance, AgentRuntimeStatus.Failed, now, recoveryReason);
        HandleAlwaysOnTermination(instance, AgentRuntimeStatus.Failed, now, settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditOutcomeAsync(instance, AgentRuntimeStatus.Failed, cancellationToken);
    }

    private async Task RecoverInterruptedStartAsync(
        AgentRuntimeInstance instance,
        AgentRuntimeGlobalSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var handle = TryGetHandle(instance);
        if (handle is null)
        {
            const string missingHandleReason = "Isolation startup was interrupted before a durable provider handle was returned; the boot lease will reap any partial VM.";
            Transition(instance, AgentRuntimeStatus.StartFailed, now, missingHandleReason);
            HandleAlwaysOnTermination(instance, AgentRuntimeStatus.StartFailed, now, settings);
            await dbContext.SaveChangesAsync(cancellationToken);
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.StartFailed, cancellationToken);
            return;
        }
        try
        {
            var status = await workloads.InspectAsync(handle, cancellationToken);
            if (status?.State == IsolationWorkloadState.Running)
            {
                instance.RuntimeDeadlineAt = now.AddSeconds(instance.AgentInstallation!.Schedule!.MaxRuntimeSeconds);
                Transition(
                    instance,
                    AgentRuntimeStatus.WaitingForMcpSession,
                    now,
                    $"Recovered isolated workload {handle.ProviderInstanceId}; awaiting broker session establishment.");
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            if (status is not null)
            {
                await workloads.DestroyAsync(handle, cancellationToken);
            }
            instance.ProviderInstanceId = null;
            instance.IsolationProviderId = null;
        }
        catch (AgentWorkloadException exception)
        {
            logger.LogWarning(exception, "Interrupted start cleanup failed for runtime {RuntimeInstanceId}.", instance.Id);
            instance.LogExcerpt = $"Could not recover interrupted isolation start: {exception.Message}";
        }

        const string recoveryReason = "Isolated workload startup was interrupted; retry to start a fresh disposable VM.";
        Transition(instance, AgentRuntimeStatus.StartFailed, now, recoveryReason);
        HandleAlwaysOnTermination(instance, AgentRuntimeStatus.StartFailed, now, settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditOutcomeAsync(instance, AgentRuntimeStatus.StartFailed, cancellationToken);
    }

    private async Task<bool> ClaimAndQueueAsync(Guid scheduleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var schedule = await dbContext.AgentSchedules.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == scheduleId, cancellationToken);
        if (schedule?.NextTickAt is null || schedule.NextTickAt > now || schedule.AgentInstallation?.IsEnabled != true) return false;
        var runtimeEligibility = await EvaluateEligibilityAsync(schedule.AgentInstallationId, cancellationToken);
        if (!runtimeEligibility.IsEligible)
        {
            logger.LogWarning("Prevented schedule runtime for installation {InstallationId}: {Reason}",
                schedule.AgentInstallationId, runtimeEligibility.Reason);
            return false;
        }
        var claimedTickAt = schedule.NextTickAt.Value;
        schedule.LastTickAt = now;
        schedule.RunRequestedAt = null;
        schedule.NextTickAt = schedule.ActivationMode switch
        {
            ActivationMode.Scheduled => now.AddSeconds(schedule.TickFrequencySeconds),
            ActivationMode.AlwaysOn => null,
            _ => null
        };

        var active = await dbContext.AgentRuntimeInstances.Where(x => x.AgentInstallationId == schedule.AgentInstallationId && (x.Status == AgentRuntimeStatus.Queued || WorkloadActiveStatuses.Contains(x.Status))).OrderBy(x => x.QueuedAt).ToListAsync(cancellationToken);
        var cancelPrevious = new List<AgentRuntimeInstance>();
        var tickOutcome = "queued";
        if (active.Count > 0 && schedule.OverlapPolicy == OverlapPolicy.Skip)
        {
            tickOutcome = "skipped";
            // Always-on reconciliation may queue the runtime immediately before its initial
            // schedule tick is claimed. That is startup coordination, not a failed run worth
            // surfacing in runtime history. Scheduled overlap skips remain recorded.
            if (schedule.ActivationMode != ActivationMode.AlwaysOn)
            {
                AddTerminalInstance(schedule.AgentInstallationId, AgentRuntimeStatus.Skipped, now, "Skipped because a prior runtime is active.");
            }
        }
        else
        {
            if (active.Count > 0 && schedule.OverlapPolicy == OverlapPolicy.CancelPrevious)
                cancelPrevious.AddRange(active);
            var instance = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = schedule.AgentInstallationId, QueuedAt = now };
            instance.Events.Add(new AgentRuntimeEvent { Id = Guid.NewGuid(), AgentRuntimeInstanceId = instance.Id, Status = AgentRuntimeStatus.Queued, Reason = $"Claimed schedule tick {claimedTickAt:O}.", OccurredAt = now });
            dbContext.AgentRuntimeInstances.Add(instance);
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            AgentRuntimeMetrics.Tick(schedule.ActivationMode.ToString(), tickOutcome);
            await auditWriter.WriteAsync("agent-runtime.schedule.tick", nameof(AgentSchedule), schedule.Id,
                $"Schedule tick {tickOutcome} for installation {schedule.AgentInstallationId}.", cancellationToken: cancellationToken);
            foreach (var prior in cancelPrevious)
                await StopAndFinishAsync(prior, AgentRuntimeStatus.Cancelled, "Cancelled by overlap policy.", now, cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Schedule {ScheduleId} was claimed by another worker.", scheduleId);
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<bool> TryStartAsync(AgentRuntimeInstance instance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var installation = instance.AgentInstallation!;
        var settings = await SettingsAsync(cancellationToken);
        var package = installation.PackageVersion!;
        var runtimeEligibility = await EvaluateEligibilityAsync(installation.Id, cancellationToken);
        if (!runtimeEligibility.IsEligible)
        {
            Transition(instance, AgentRuntimeStatus.PolicyDenied, now,
                runtimeEligibility.Reason ?? "Runtime eligibility was denied.");
            await dbContext.SaveChangesAsync(cancellationToken);
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.PolicyDenied, cancellationToken);
            return true;
        }
        if (!installation.IsEnabled || installation.Schedule?.IsEnabled != true || installation.Grant is null)
        {
            Transition(instance, AgentRuntimeStatus.PolicyDenied, now, "The installation, schedule, or approved grant is disabled or unavailable.");
            await dbContext.SaveChangesAsync(cancellationToken);
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.PolicyDenied, cancellationToken);
            return true;
        }

        if (package.Status == AgentPackageVersionStatus.Approved)
        {
            var buildInProgress = package.BuildJobs.Any(x => x.Status is
                AgentBuildStatus.Queued or AgentBuildStatus.Cloning or AgentBuildStatus.Building);
            if (buildInProgress)
            {
                logger.LogDebug(
                    "Runtime {RuntimeInstanceId} is waiting for package {PackageVersionId} to finish building.",
                    instance.Id,
                    package.Id);
                return false;
            }

            await FailBeforeStartAsync(instance, now, "The approved package has no active build.", cancellationToken);
            return true;
        }

        if (package.Status == AgentPackageVersionStatus.Failed)
        {
            var buildFailure = package.BuildJobs
                .OrderByDescending(x => x.Attempt)
                .Select(x => x.FailureMessage)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            await FailBeforeStartAsync(
                instance,
                now,
                buildFailure is null ? "The agent package build failed." : $"The agent package build failed: {buildFailure}",
                cancellationToken,
                suppressFurtherAlwaysOnStarts: true);
            return true;
        }

        if (package.Status is AgentPackageVersionStatus.Previewed or AgentPackageVersionStatus.Revoked)
        {
            Transition(instance, AgentRuntimeStatus.PolicyDenied, now, $"The agent package is {package.Status} and is not approved to run.");
            await dbContext.SaveChangesAsync(cancellationToken);
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.PolicyDenied, cancellationToken);
            return true;
        }

        if (string.IsNullOrWhiteSpace(package.PackageDigest) ||
            string.IsNullOrWhiteSpace(package.ArtifactSignature) ||
            string.IsNullOrWhiteSpace(package.ProjectPath))
        {
            await FailBeforeStartAsync(instance, now, "The built agent artifact is unsigned or incomplete.", cancellationToken);
            return true;
        }

        var globalCount = await dbContext.AgentRuntimeInstances.CountAsync(x => WorkloadActiveStatuses.Contains(x.Status), cancellationToken);
        var businessCount = await dbContext.AgentRuntimeInstances.CountAsync(x => WorkloadActiveStatuses.Contains(x.Status) && x.AgentInstallation!.BusinessId == installation.BusinessId, cancellationToken);
        var installationCount = await dbContext.AgentRuntimeInstances.CountAsync(x => WorkloadActiveStatuses.Contains(x.Status) && x.AgentInstallationId == installation.Id, cancellationToken);
        if (globalCount >= settings.GlobalMaxActiveWorkloads || businessCount >= settings.PerBusinessMaxActiveWorkloads || installationCount >= settings.PerInstallationMaxActiveWorkloads)
        {
            var capacityReason = $"Waiting for isolated workload capacity: global {globalCount}/{settings.GlobalMaxActiveWorkloads}, business {businessCount}/{settings.PerBusinessMaxActiveWorkloads}, installation {installationCount}/{settings.PerInstallationMaxActiveWorkloads}.";
            if (!string.Equals(instance.Reason, capacityReason, StringComparison.Ordinal))
            {
                Transition(instance, AgentRuntimeStatus.Queued, now, capacityReason);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return false;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        instance.BrokerTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        instance.IsolationProviderId = null;
        instance.ProviderInstanceId = null;
        Transition(instance, AgentRuntimeStatus.Starting, now, "Creating a certified hardware-isolated runtime VM.");
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(TimeSpan.FromSeconds(settings.WorkloadStartTimeoutSeconds));
            var entrypoint = Path.GetFileNameWithoutExtension(package.ProjectPath);
            var runtimeRequest = ReadRuntimeRequest(package.ManifestJson);
            var isDeveloperRuntime = string.Equals(
                runtimeRequest.EnvironmentProfile,
                "software-development-polyglot-v1",
                StringComparison.Ordinal);
            if (isDeveloperRuntime &&
                !string.Equals(runtimeRequest.WorkspaceAccess, "ReadWrite", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The software development runtime requires ReadWrite workspace access.");
            var runtimeOptions = options.Value;
            var employeeIdentity = await dbContext.CoreOrganizationUsers
                .AsNoTracking()
                .Where(x => x.AgentInstallationId == installation.Id && x.IsActive)
                .Select(x => new
                {
                    x.DisplayName,
                    RoleName = x.Role == null ? null : x.Role.Name
                })
                .SingleOrDefaultAsync(startTimeout.Token);
            var guestImage = await guestImages.ResolveAsync(new GuestImageResolutionRequest(
                runtimeOptions.RuntimeGuestImageId,
                runtimeOptions.RuntimeGuestImageVersion,
                runtimeOptions.RuntimeGuestOperatingSystem,
                runtimeOptions.RuntimeGuestArchitecture,
                AgentTrustLevel.UntrustedRepository,
                "1.0",
                runtimeOptions.PreferredIsolationProviderId,
                runtimeOptions.RuntimeGuestImageDigest,
                runtimeOptions.RequiredCertificationSuiteVersion), startTimeout.Token);
            var guestDigest = guestImage.Digest;
            var artifactDigest = NormalizeDigest(package.PackageDigest, "agent artifact");
            var artifact = new AgentArtifactReference(
                artifactDigest,
                package.ArtifactSignature,
                package.ArtifactFormatVersion,
                package.ArtifactOperatingSystem,
                package.ArtifactArchitecture);
            var lease = new BrokerChannelLease(
                Guid.NewGuid(),
                "1.0",
                token,
                guestDigest,
                artifactDigest,
                now.AddSeconds(installation.Schedule.MaxRuntimeSeconds).AddMinutes(5));
            var limits = new WorkloadResourceLimits(
                Math.Max(1, (int)Math.Ceiling(installation.Grant.CpuPercent / 100d)),
                installation.Grant.CpuPercent,
                installation.Grant.MemoryMb,
                runtimeOptions.RuntimeWritableDiskMb,
                settings.DefaultWorkloadProcessLimit,
                checked(settings.DefaultWorkloadLogLimitMb * 1024 * 1024),
                TimeSpan.FromSeconds(installation.Schedule.MaxRuntimeSeconds));
            var handle = await workloads.CreateAndStartAsync(
                new RuntimeWorkloadSpecification(
                    instance.Id,
                    guestImage,
                    limits,
                    lease,
                    artifact,
                    new RuntimeAgentIdentity(
                        installation.Id,
                        installation.BusinessId,
                        instance.TickId,
                        string.IsNullOrWhiteSpace(employeeIdentity?.DisplayName)
                            ? package.AgentName
                            : employeeIdentity.DisplayName,
                        employeeIdentity?.RoleName),
                    [entrypoint]),
                AgentTrustLevel.UntrustedRepository,
                runtimeOptions.PreferredIsolationProviderId,
                startTimeout.Token);
            instance.IsolationProviderId = handle.ProviderId;
            instance.ProviderInstanceId = handle.ProviderInstanceId;
            instance.RuntimeDeadlineAt = now.AddSeconds(installation.Schedule.MaxRuntimeSeconds);
            Transition(instance, AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow,
                $"Hardware-isolated workload {handle.ProviderInstanceId} started with broker-only communication; awaiting authenticated broker session.");
            AgentRuntimeMetrics.WorkloadStarted();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TryRemoveFailedStartAsync(instance, cancellationToken);
            instance.LogExcerpt = "Certified VM launch timed out.";
            Transition(instance, AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, "Certified VM start timed out.");
        }
        catch (Exception exception) when (exception is AgentWorkloadException or IsolationUnavailableException or InvalidOperationException)
        {
            logger.LogError(exception, "Failed to start runtime {RuntimeInstanceId}", instance.Id);
            await TryRemoveFailedStartAsync(instance, cancellationToken);
            instance.LogExcerpt = $"Certified VM launch failed.{Environment.NewLine}{exception.Message}";
            Transition(instance, AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, exception.Message);
        }
        if (instance.Status == AgentRuntimeStatus.StartFailed)
            HandleAlwaysOnTermination(instance, AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (instance.Status == AgentRuntimeStatus.WaitingForMcpSession)
            await auditWriter.WriteAsync("agent-runtime.workload.started", nameof(AgentRuntimeInstance), instance.Id,
                $"Started certified workload {instance.IsolationProviderId}/{instance.ProviderInstanceId} for installation {instance.AgentInstallationId}.", cancellationToken: cancellationToken);
        else if (instance.Status == AgentRuntimeStatus.StartFailed)
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.StartFailed, cancellationToken);
        return true;
    }

    private static (string? EnvironmentProfile, string WorkspaceAccess) ReadRuntimeRequest(
        string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("runtime", out var runtime))
            return (null, "None");
        var environmentProfile = runtime.TryGetProperty("environmentProfile", out var profile)
            ? profile.GetString()
            : null;
        var workspaceAccess = runtime.TryGetProperty("workspaceAccess", out var access)
            ? access.GetString() ?? "None"
            : "None";
        return (environmentProfile, workspaceAccess);
    }

    private async Task FailBeforeStartAsync(
        AgentRuntimeInstance instance,
        DateTimeOffset occurredAt,
        string reason,
        CancellationToken cancellationToken,
        bool suppressFurtherAlwaysOnStarts = false)
    {
        Transition(instance, AgentRuntimeStatus.Failed, occurredAt, reason);
        if (suppressFurtherAlwaysOnStarts)
        {
            SuppressAlwaysOnStartup(instance, occurredAt);
        }
        else
        {
            HandleAlwaysOnTermination(instance, AgentRuntimeStatus.Failed, occurredAt, await SettingsAsync(cancellationToken));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditOutcomeAsync(instance, AgentRuntimeStatus.Failed, cancellationToken);
    }

    private async Task TryRemoveFailedStartAsync(AgentRuntimeInstance instance, CancellationToken cancellationToken)
    {
        var handle = TryGetHandle(instance);
        if (handle is null) return;
        try
        {
            if (await workloads.InspectAsync(handle, cancellationToken) is not null)
                await workloads.DestroyAsync(handle, cancellationToken);
            instance.ProviderInstanceId = null;
            instance.IsolationProviderId = null;
        }
        catch (AgentWorkloadException exception)
        {
            logger.LogWarning(exception, "Failed-start resource cleanup will be retried for runtime {RuntimeInstanceId}.", instance.Id);
        }
    }

    private async Task StopAndFinishAsync(AgentRuntimeInstance instance, AgentRuntimeStatus terminal, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Transition(instance, AgentRuntimeStatus.Stopping, now, reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        var settings = await SettingsAsync(cancellationToken);
        if (TryGetHandle(instance) is { } handle)
        {
            try
            {
                var maximumLogBytes = Math.Min(settings.DefaultWorkloadLogLimitMb * 1024 * 1024, 64 * 1024);
                var providerLogs = await workloads.GetLogsAsync(handle, maximumLogBytes, cancellationToken);
                // The authenticated guest broker streams the useful process diagnostics to
                // Headquarters. Some remote providers have no separate host-side log buffer;
                // never let that empty result erase the broker-retained failure details.
                if (!string.IsNullOrWhiteSpace(providerLogs))
                    instance.LogExcerpt = providerLogs;
            }
            catch (AgentWorkloadException exception)
            {
                logger.LogWarning(exception, "Could not retain logs for runtime {RuntimeInstanceId}.", instance.Id);
            }
            try
            {
                await workloads.StopAsync(handle, TimeSpan.FromSeconds(settings.WorkloadStopGraceSeconds), cancellationToken);
                if (settings.RemoveWorkloadsAfterCompletion)
                {
                    await workloads.DestroyAsync(handle, cancellationToken);
                    instance.ProviderInstanceId = null;
                    instance.IsolationProviderId = null;
                }
                AgentRuntimeMetrics.WorkloadStopped(terminal.ToString());
                await auditWriter.WriteAsync("agent-runtime.workload.stopped", nameof(AgentRuntimeInstance), instance.Id,
                    $"Stopped isolated workload {handle.ProviderId}/{handle.ProviderInstanceId}: {reason}", cancellationToken: cancellationToken);
            }
            catch (AgentWorkloadException exception) { logger.LogWarning(exception, "Isolated workload cleanup failed for runtime {RuntimeInstanceId}", instance.Id); }
        }
        Transition(instance, terminal, DateTimeOffset.UtcNow, reason);
        var sessions = await dbContext.McpAgentSessions
            .Where(x => x.RuntimeInstanceId == instance.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = DateTimeOffset.UtcNow;
            session.RevocationReason = $"Runtime ended as {terminal}.";
        }
        if (terminal == AgentRuntimeStatus.Completed && instance.AgentInstallation?.Schedule is { } schedule)
            schedule.LastCompletedAt = DateTimeOffset.UtcNow;
        HandleAlwaysOnTermination(instance, terminal, DateTimeOffset.UtcNow, settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditOutcomeAsync(instance, terminal, cancellationToken);
    }

    private void AddTerminalInstance(Guid installationId, AgentRuntimeStatus status, DateTimeOffset now, string reason)
    {
        var instance = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installationId, QueuedAt = now };
        instance.TransitionTo(status, now, reason);
        instance.Events.Add(new AgentRuntimeEvent { Id = Guid.NewGuid(), AgentRuntimeInstanceId = instance.Id, Status = status, Reason = reason, OccurredAt = now });
        dbContext.AgentRuntimeInstances.Add(instance);
    }

    private void Transition(AgentRuntimeInstance instance, AgentRuntimeStatus status, DateTimeOffset at, string reason)
    {
        instance.TransitionTo(status, at, reason);
        dbContext.AgentRuntimeEvents.Add(new AgentRuntimeEvent { Id = Guid.NewGuid(), AgentRuntimeInstanceId = instance.Id, Status = status, Reason = reason, OccurredAt = at });
        logger.LogInformation("Agent runtime {RuntimeInstanceId} transitioned to {RuntimeStatus}: {Reason}", instance.Id, status, reason);
        if (AgentRuntimeInstance.IsTerminal(status))
            AgentRuntimeMetrics.RuntimeOutcome(status, instance.StartedAt is { } started ? at - started : null);
    }

    private Task AuditOutcomeAsync(AgentRuntimeInstance instance, AgentRuntimeStatus status, CancellationToken cancellationToken)
    {
        var eventType = status switch
        {
            AgentRuntimeStatus.Completed => "agent-runtime.completed",
            AgentRuntimeStatus.RuntimeTimedOut or AgentRuntimeStatus.McpSessionTimedOut => "agent-runtime.timeout",
            AgentRuntimeStatus.PolicyDenied => "agent-runtime.policy-denied",
            AgentRuntimeStatus.StartFailed or AgentRuntimeStatus.Failed or AgentRuntimeStatus.ExitedWithoutCompletion => "agent-runtime.failed",
            AgentRuntimeStatus.Cancelled => "agent-runtime.cancelled",
            _ => "agent-runtime.outcome"
        };
        return auditWriter.WriteAsync(eventType, nameof(AgentRuntimeInstance), instance.Id,
            $"Runtime ended as {status}: {instance.Reason}", cancellationToken: cancellationToken);
    }

    private static void HandleAlwaysOnTermination(
        AgentRuntimeInstance instance,
        AgentRuntimeStatus terminal,
        DateTimeOffset occurredAt,
        AgentRuntimeGlobalSettings settings)
    {
        var schedule = instance.AgentInstallation?.Schedule;
        if (schedule?.ActivationMode != ActivationMode.AlwaysOn || !schedule.IsEnabled || instance.AgentInstallation?.IsEnabled != true)
            return;

        var startupFailed = instance.McpSessionEstablishedAt is null && terminal is
            AgentRuntimeStatus.StartFailed or
            AgentRuntimeStatus.Failed or
            AgentRuntimeStatus.ExitedWithoutCompletion or
            AgentRuntimeStatus.McpSessionTimedOut;
        if (startupFailed)
        {
            schedule.ConsecutiveStartupFailures++;
            if (schedule.ConsecutiveStartupFailures >= MaximumAlwaysOnStartupAttempts)
            {
                schedule.AutomaticStartSuppressedAt = occurredAt;
                schedule.NextTickAt = null;
                return;
            }

            schedule.NextTickAt = occurredAt;
            return;
        }

        var failed = terminal is not (AgentRuntimeStatus.Completed or AgentRuntimeStatus.Cancelled);
        if (settings.DefaultRestartPolicy == RestartPolicy.Always ||
            (settings.DefaultRestartPolicy == RestartPolicy.OnFailure && failed))
            schedule.NextTickAt = DateTimeOffset.UtcNow;
    }

    private static void SuppressAlwaysOnStartup(
        AgentRuntimeInstance instance,
        DateTimeOffset occurredAt)
    {
        var schedule = instance.AgentInstallation?.Schedule;
        if (schedule?.ActivationMode != ActivationMode.AlwaysOn ||
            !schedule.IsEnabled ||
            instance.AgentInstallation?.IsEnabled != true)
        {
            return;
        }

        schedule.ConsecutiveStartupFailures = Math.Max(
            schedule.ConsecutiveStartupFailures + 1,
            MaximumAlwaysOnStartupAttempts);
        schedule.AutomaticStartSuppressedAt = occurredAt;
        schedule.NextTickAt = null;
    }

    private async Task<AgentRuntimeGlobalSettings> SettingsAsync(CancellationToken cancellationToken)
        => await dbContext.AgentRuntimeGlobalSettings.SingleAsync(cancellationToken);

    private Task<AgentRuntimeEligibility> EvaluateEligibilityAsync(
        Guid installationId, CancellationToken cancellationToken) =>
        eligibility.EvaluateAsync(installationId, cancellationToken);

    private static IsolationWorkloadHandle? TryGetHandle(AgentRuntimeInstance instance) =>
        string.IsNullOrWhiteSpace(instance.IsolationProviderId) || string.IsNullOrWhiteSpace(instance.ProviderInstanceId)
            ? null
            : new IsolationWorkloadHandle(
                instance.IsolationProviderId,
                instance.Id,
                instance.ProviderInstanceId,
                WorkloadKind.Runtime);

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"The {name} is not configured.");

    private static string NormalizeDigest(string value, string name)
    {
        var normalized = value.StartsWith("sha256:", StringComparison.Ordinal) ? value : $"sha256:{value}";
        if (normalized.Length != 71 || normalized.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new InvalidOperationException($"The {name} must be an immutable lowercase SHA-256 digest.");
        return normalized;
    }
}
