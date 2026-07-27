using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeManager(
    CSweetDbContext dbContext,
    IAgentContainerRunner containers,
    IAuditEventWriter auditWriter,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<AgentRuntimeManager> logger) : IPluginRuntimeManager
{
    private const int MaximumAlwaysOnStartupAttempts = 3;
    private static readonly AgentRuntimeStatus[] ContainerActiveStatuses =
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
                    (x.Status == AgentRuntimeStatus.Queued || ContainerActiveStatuses.Contains(x.Status)),
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (activeRuntime is not null)
        {
            if (interactive && activeRuntime.AgentInstallation?.Schedule?.ActivationMode != ActivationMode.AlwaysOn)
            {
                activeRuntime.IsInteractive = true;
                activeRuntime.LastInteractiveActivityAt = now;
                activeRuntime.IdleDeadlineAt = now.AddSeconds(
                    Math.Max(1, options.Value.InteractiveIdleTimeoutSeconds));
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return false;
        }

        var activationMode = await dbContext.AgentSchedules
            .Where(x => x.AgentInstallationId == installationId)
            .Select(x => x.ActivationMode)
            .SingleAsync(cancellationToken);
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
                            (x.Status == AgentRuntimeStatus.Queued || ContainerActiveStatuses.Contains(x.Status)),
                        cancellationToken);
                if (winner.AgentInstallation?.Schedule?.ActivationMode != ActivationMode.AlwaysOn)
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
        var activeRuntime = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .OrderByDescending(x => x.QueuedAt)
            .FirstOrDefaultAsync(
                x => x.AgentInstallationId == installationId &&
                    (x.Status == AgentRuntimeStatus.Queued || ContainerActiveStatuses.Contains(x.Status)),
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
                    ContainerActiveStatuses.Contains(runtime.Status)))
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
        var instances = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)!.ThenInclude(x => x!.BuildJobs)
            .Include(x => x.Events)
            .Where(x => x.Status == AgentRuntimeStatus.Queued || ContainerActiveStatuses.Contains(x.Status))
            .OrderBy(x => x.QueuedAt).ToListAsync(cancellationToken);

        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instance.Status == AgentRuntimeStatus.Stopping)
            {
                var settings = await SettingsAsync(cancellationToken);
                var stoppingAt = instance.Events
                    .Where(x => x.Status == AgentRuntimeStatus.Stopping)
                    .MaxBy(x => x.OccurredAt)?.OccurredAt ?? instance.StartedAt ?? instance.QueuedAt;
                if (stoppingAt.AddSeconds(settings.ContainerStopGraceSeconds + 5) <= now)
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
                if (startingAt.AddSeconds(settings.ContainerStartTimeoutSeconds + 5) <= now)
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
            if (instance.Status == AgentRuntimeStatus.WaitingForMcpSession && instance.StartedAt?.AddSeconds((await SettingsAsync(cancellationToken)).McpSessionTimeoutSeconds) <= now)
            {
                await StopAndFinishAsync(instance, AgentRuntimeStatus.McpSessionTimedOut, "MCP session establishment timed out.", now, cancellationToken);
                changed++;
                continue;
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
            if (instance.ContainerId is not null && instance.Status is AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Running)
            {
                var status = await containers.InspectAsync(instance.ContainerId, cancellationToken);
                if (status is null || status.State is AgentContainerState.Exited or AgentContainerState.Dead)
                {
                    var terminal = status?.ExitCode is 0 ? AgentRuntimeStatus.ExitedWithoutCompletion : AgentRuntimeStatus.Failed;
                    await StopAndFinishAsync(instance, terminal, status?.Error ?? "Container exited without a completion event.", now, cancellationToken);
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
        if (instance.ContainerId is { } containerId)
        {
            try
            {
                var status = await containers.InspectAsync(containerId, cancellationToken);
                if (status is not null)
                {
                    await containers.RemoveAsync(containerId, force: true, cancellationToken: cancellationToken);
                }
                await RemoveRuntimeNetworkAsync(instance, cancellationToken);
                instance.ContainerId = null;
            }
            catch (AgentContainerException exception)
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
        var containerName = instance.ContainerName ?? $"csweet-agent-{instance.Id:N}";
        try
        {
            var status = await containers.InspectAsync(containerName, cancellationToken);
            if (status?.State == AgentContainerState.Running)
            {
                instance.ContainerId = status.ContainerId;
                instance.RuntimeDeadlineAt = now.AddSeconds(instance.AgentInstallation!.Schedule!.MaxRuntimeSeconds);
                Transition(
                    instance,
                    AgentRuntimeStatus.WaitingForMcpSession,
                    now,
                    $"Recovered running container {status.ContainerId}; awaiting MCP session establishment at {options.Value.McpEndpoint}.");
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            if (status is not null)
            {
                await containers.RemoveAsync(containerName, force: true, cancellationToken: cancellationToken);
            }
            await RemoveRuntimeNetworkAsync(instance, cancellationToken);
            instance.ContainerId = null;
        }
        catch (AgentContainerException exception)
        {
            logger.LogWarning(exception, "Interrupted start cleanup failed for runtime {RuntimeInstanceId}.", instance.Id);
            instance.ContainerId = containerName;
            instance.LogExcerpt = $"Could not recover interrupted container start: {exception.Message}";
        }

        const string recoveryReason = "Container startup was interrupted before completion; retry to start a fresh runtime.";
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
        var claimedTickAt = schedule.NextTickAt.Value;
        schedule.LastTickAt = now;
        schedule.RunRequestedAt = null;
        schedule.NextTickAt = schedule.ActivationMode switch
        {
            ActivationMode.Periodic => now.AddSeconds(schedule.TickFrequencySeconds),
            ActivationMode.AlwaysOn => null,
            _ => null
        };

        var active = await dbContext.AgentRuntimeInstances.Where(x => x.AgentInstallationId == schedule.AgentInstallationId && (x.Status == AgentRuntimeStatus.Queued || ContainerActiveStatuses.Contains(x.Status))).OrderBy(x => x.QueuedAt).ToListAsync(cancellationToken);
        var cancelPrevious = new List<AgentRuntimeInstance>();
        var tickOutcome = "queued";
        if (active.Count > 0 && schedule.OverlapPolicy == OverlapPolicy.Skip)
        {
            tickOutcome = "skipped";
            // Always-on reconciliation may queue the runtime immediately before its initial
            // schedule tick is claimed. That is startup coordination, not a failed run worth
            // surfacing in runtime history. Periodic overlap skips remain recorded.
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
                cancellationToken);
            return true;
        }

        if (package.Status is AgentPackageVersionStatus.Previewed or AgentPackageVersionStatus.Revoked)
        {
            Transition(instance, AgentRuntimeStatus.PolicyDenied, now, $"The agent package is {package.Status} and is not approved to run.");
            await dbContext.SaveChangesAsync(cancellationToken);
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.PolicyDenied, cancellationToken);
            return true;
        }

        if (string.IsNullOrWhiteSpace(package.PackagePath) || string.IsNullOrWhiteSpace(package.ProjectPath))
        {
            await FailBeforeStartAsync(instance, now, "The built agent package is incomplete.", cancellationToken);
            return true;
        }

        var globalCount = await dbContext.AgentRuntimeInstances.CountAsync(x => ContainerActiveStatuses.Contains(x.Status), cancellationToken);
        var businessCount = await dbContext.AgentRuntimeInstances.CountAsync(x => ContainerActiveStatuses.Contains(x.Status) && x.AgentInstallation!.BusinessId == installation.BusinessId, cancellationToken);
        var installationCount = await dbContext.AgentRuntimeInstances.CountAsync(x => ContainerActiveStatuses.Contains(x.Status) && x.AgentInstallationId == installation.Id, cancellationToken);
        if (globalCount >= settings.GlobalMaxActiveContainers || businessCount >= settings.PerBusinessMaxActiveContainers || installationCount >= settings.PerInstallationMaxActiveContainers)
        {
            var capacityReason = $"Waiting for container capacity: global {globalCount}/{settings.GlobalMaxActiveContainers}, business {businessCount}/{settings.PerBusinessMaxActiveContainers}, installation {installationCount}/{settings.PerInstallationMaxActiveContainers}.";
            if (!string.Equals(instance.Reason, capacityReason, StringComparison.Ordinal))
            {
                Transition(instance, AgentRuntimeStatus.Queued, now, capacityReason);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return false;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        instance.WorkloadTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        instance.ContainerName = $"csweet-agent-{instance.Id:N}";
        Transition(instance, AgentRuntimeStatus.Starting, now, "Starting runtime container.");
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(TimeSpan.FromSeconds(settings.ContainerStartTimeoutSeconds));
            var entryAssembly = Path.GetFileNameWithoutExtension(package.ProjectPath) + ".dll";
            var status = await containers.StartAsync(new AgentContainerStartRequest(
                instance.Id, instance.TickId, installation.Id, package.AgentId, installation.BusinessId,
                instance.ContainerName,
                DotNetAgentImageResolver.ResolveRuntimeImage(
                    settings.DotNetRuntimeBaseImage,
                    package.TargetFramework),
                package.PackagePath, entryAssembly,
                options.Value.McpEndpoint, token, "/app/csweet-plugin.json", RuntimeNetworkName(instance),
                installation.Grant.MemoryMb, installation.Grant.CpuPercent, settings.DefaultContainerPidsLimit,
                installation.Schedule.MaxRuntimeSeconds,
                null, options.Value.McpGatewayContainer), startTimeout.Token);
            instance.ContainerId = status.ContainerId;
            instance.RuntimeDeadlineAt = now.AddSeconds(installation.Schedule.MaxRuntimeSeconds);
            Transition(instance, AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow,
                $"Container {status.ContainerId} started on isolated runtime network; awaiting MCP session establishment at {options.Value.McpEndpoint}.");
            AgentRuntimeMetrics.ContainerStarted();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TryRemoveFailedStartAsync(instance, cancellationToken);
            instance.LogExcerpt =
                $"Container launch timed out. Image: {settings.DotNetRuntimeBaseImage}; network: {options.Value.DockerNetworkName}; MCP endpoint: {options.Value.McpEndpoint}.";
            Transition(instance, AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, "Container start timed out.");
        }
        catch (Exception exception) when (exception is AgentContainerException or InvalidOperationException)
        {
            logger.LogError(exception, "Failed to start runtime {RuntimeInstanceId}", instance.Id);
            await TryRemoveFailedStartAsync(instance, cancellationToken);
            instance.LogExcerpt =
                $"Container launch failed. Image: {settings.DotNetRuntimeBaseImage}; network: {options.Value.DockerNetworkName}; MCP endpoint: {options.Value.McpEndpoint}.{Environment.NewLine}{exception.Message}";
            Transition(instance, AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, exception.Message);
        }
        if (instance.Status == AgentRuntimeStatus.StartFailed)
            HandleAlwaysOnTermination(instance, AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (instance.Status == AgentRuntimeStatus.WaitingForMcpSession)
            await auditWriter.WriteAsync("agent-runtime.container.started", nameof(AgentRuntimeInstance), instance.Id,
                $"Started container {instance.ContainerId} for installation {instance.AgentInstallationId}.", cancellationToken: cancellationToken);
        else if (instance.Status == AgentRuntimeStatus.StartFailed)
            await AuditOutcomeAsync(instance, AgentRuntimeStatus.StartFailed, cancellationToken);
        return true;
    }

    private async Task FailBeforeStartAsync(
        AgentRuntimeInstance instance,
        DateTimeOffset occurredAt,
        string reason,
        CancellationToken cancellationToken)
    {
        Transition(instance, AgentRuntimeStatus.Failed, occurredAt, reason);
        HandleAlwaysOnTermination(instance, AgentRuntimeStatus.Failed, occurredAt, await SettingsAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditOutcomeAsync(instance, AgentRuntimeStatus.Failed, cancellationToken);
    }

    private async Task TryRemoveFailedStartAsync(AgentRuntimeInstance instance, CancellationToken cancellationToken)
    {
        var containerName = instance.ContainerName ?? $"csweet-agent-{instance.Id:N}";
        try
        {
            if (await containers.InspectAsync(containerName, cancellationToken) is not null)
                await containers.RemoveAsync(containerName, force: true, cancellationToken: cancellationToken);
            await RemoveRuntimeNetworkAsync(instance, cancellationToken);
            instance.ContainerId = null;
        }
        catch (AgentContainerException exception)
        {
            // Retain a name-based cleanup marker so the background cleanup service can retry
            // both the container and its per-runtime network after this attempt is terminal.
            instance.ContainerId = containerName;
            logger.LogWarning(exception, "Failed-start resource cleanup will be retried for runtime {RuntimeInstanceId}.", instance.Id);
        }
    }

    private async Task StopAndFinishAsync(AgentRuntimeInstance instance, AgentRuntimeStatus terminal, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Transition(instance, AgentRuntimeStatus.Stopping, now, reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        var settings = await SettingsAsync(cancellationToken);
        if (instance.ContainerId is not null)
        {
            var containerId = instance.ContainerId;
            try
            {
                var maximumLogBytes = Math.Min(settings.DefaultContainerLogLimitMb * 1024 * 1024, 64 * 1024);
                instance.LogExcerpt = await containers.GetLogsAsync(containerId, maximumLogBytes, cancellationToken);
            }
            catch (AgentContainerException exception)
            {
                logger.LogWarning(exception, "Could not retain logs for runtime {RuntimeInstanceId}.", instance.Id);
            }
            try
            {
                await containers.StopAsync(containerId, TimeSpan.FromSeconds(settings.ContainerStopGraceSeconds), cancellationToken);
                if (settings.RemoveContainersAfterCompletion)
                {
                    await containers.RemoveAsync(containerId, cancellationToken: cancellationToken);
                    await RemoveRuntimeNetworkAsync(instance, cancellationToken);
                    instance.ContainerId = null;
                }
                AgentRuntimeMetrics.ContainerStopped(terminal.ToString());
                await auditWriter.WriteAsync("agent-runtime.container.stopped", nameof(AgentRuntimeInstance), instance.Id,
                    $"Stopped container {containerId}: {reason}", cancellationToken: cancellationToken);
            }
            catch (AgentContainerException exception) { logger.LogWarning(exception, "Container cleanup failed for runtime {RuntimeInstanceId}", instance.Id); }
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

    private async Task<AgentRuntimeGlobalSettings> SettingsAsync(CancellationToken cancellationToken)
        => await dbContext.AgentRuntimeGlobalSettings.SingleAsync(cancellationToken);

    private string RuntimeNetworkName(AgentRuntimeInstance instance)
        => $"{options.Value.DockerNetworkName}-{instance.Id:N}";

    private Task RemoveRuntimeNetworkAsync(AgentRuntimeInstance instance, CancellationToken cancellationToken)
        => containers.RemoveNetworkAsync(
            RuntimeNetworkName(instance),
            options.Value.McpGatewayContainer,
            cancellationToken);
}
