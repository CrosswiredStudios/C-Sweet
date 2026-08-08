using CSweet.Application.Setup;
using CSweet.AgentRuntime.Abstractions;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeManagerTests
{
    [Fact]
    public async Task InteractiveEnsure_ReusesRuntimeAndBecomesReadyAfterMcpSession()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);
        var interactive = new AgentInteractiveRuntimeService(db, manager);

        var starting = await interactive.EnsureReadyAsync(installation.Id);
        var reused = await interactive.EnsureReadyAsync(installation.Id);

        Assert.Equal(AgentRuntimeReadinessStages.WaitingForMcpSession, starting.Stage);
        Assert.Equal(starting.RuntimeInstanceId, reused.RuntimeInstanceId);
        Assert.Single(containers.Starts);
        Assert.Single(await db.AgentRuntimeInstances.ToListAsync());

        var request = Assert.Single(containers.Starts);
        await new AgentRuntimeSignalService(db).RecordMcpSessionEstablishedAsync(
            request.RuntimeInstanceId,
            request.TickId,
            request.InstallationId,
            request.WorkloadToken);

        var ready = await interactive.GetStatusAsync(installation.Id);

        Assert.True(ready.IsReady);
        Assert.Equal(AgentRuntimeReadinessStages.Ready, ready.Stage);

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        runtime.IdleDeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        var reusedReady = await interactive.EnsureReadyAsync(installation.Id);

        Assert.True(reusedReady.IsReady);
        Assert.True(runtime.IdleDeadlineAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InteractiveEnsure_AfterFailedRuntime_StartsFreshAttempt()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var failed = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        failed.TransitionTo(AgentRuntimeStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(-1), "Missing Docker network.");
        db.AgentRuntimeInstances.Add(failed);
        await db.SaveChangesAsync();
        var containers = new FakeRunner();

        var readiness = await new AgentInteractiveRuntimeService(
            db,
            CreateManager(db, containers)).EnsureReadyAsync(installation.Id);

        Assert.Equal(AgentRuntimeReadinessStages.WaitingForMcpSession, readiness.Stage);
        Assert.NotEqual(failed.Id, readiness.RuntimeInstanceId);
        Assert.Single(containers.Starts);
        Assert.Equal(2, await db.AgentRuntimeInstances.CountAsync());
    }

    [Fact]
    public async Task InteractiveStatus_PrefersActiveRuntimeOverNewerSkippedTick()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var running = RunningInstance(installation.Id);
        var skipped = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = running.QueuedAt.AddSeconds(1)
        };
        skipped.TransitionTo(AgentRuntimeStatus.Skipped, skipped.QueuedAt, "Skipped because a prior runtime is active.");
        db.AgentRuntimeInstances.AddRange(running, skipped);
        await db.SaveChangesAsync();

        var readiness = await new AgentInteractiveRuntimeService(
            db,
            CreateManager(db, new FakeRunner())).GetStatusAsync(installation.Id);

        Assert.Equal(running.Id, readiness.RuntimeInstanceId);
        Assert.Equal(AgentRuntimeReadinessStages.Ready, readiness.Stage);
        Assert.True(readiness.IsReady);
        Assert.False(readiness.IsTerminal);
    }

    [Fact]
    public async Task InteractiveStatus_TreatsBenignTerminalRuntimeAsOffline()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var skipped = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow
        };
        skipped.TransitionTo(AgentRuntimeStatus.Skipped, skipped.QueuedAt, "Skipped because a prior runtime is active.");
        db.AgentRuntimeInstances.Add(skipped);
        await db.SaveChangesAsync();

        var readiness = await new AgentInteractiveRuntimeService(
            db,
            CreateManager(db, new FakeRunner())).GetStatusAsync(installation.Id);

        Assert.Equal(AgentRuntimeReadinessStages.Offline, readiness.Stage);
        Assert.False(readiness.IsReady);
        Assert.True(readiness.IsTerminal);
    }

    [Fact]
    public async Task AlwaysOnReconciliation_StartsOneMissingRuntime()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        installation.Schedule.NextTickAt = null;
        await db.SaveChangesAsync();
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        Assert.Equal(1, await manager.EnsureAlwaysOnRuntimesAsync());
        Assert.Equal(0, await manager.EnsureAlwaysOnRuntimesAsync());
        await manager.ReconcileAsync();

        Assert.Single(containers.Starts);
        Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task AlwaysOnReconciliation_StopsAfterThreeStartupFailuresAndRetainsDetails()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        installation.Schedule.NextTickAt = null;
        await db.SaveChangesAsync();
        var containers = new FakeRunner
        {
            InspectStatus = new IsolationWorkloadStatus(
                new IsolationWorkloadHandle("test-vm", Guid.NewGuid(), "vm-1", IsolationWorkloadKind.Runtime),
                IsolationWorkloadState.Stopped,
                IsolationTerminationReason.ProviderFailure,
                1,
                null,
                null,
                "guest-exit",
                "The agent process exited during startup."),
            Logs = "Fatal: manifest version does not match the implementation version."
        };
        var manager = CreateManager(db, containers);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(1, await manager.EnsureAlwaysOnRuntimesAsync());
            Assert.Equal(1, await manager.ReconcileAsync());
            Assert.Equal(1, await manager.ReconcileAsync());
            Assert.Equal(attempt, installation.Schedule.ConsecutiveStartupFailures);
        }

        Assert.NotNull(installation.Schedule.AutomaticStartSuppressedAt);
        Assert.Null(installation.Schedule.NextTickAt);
        Assert.Equal(0, await manager.EnsureAlwaysOnRuntimesAsync());
        Assert.Equal(3, containers.Starts.Count);
        var latest = await db.AgentRuntimeInstances.OrderByDescending(x => x.QueuedAt).FirstAsync();
        Assert.Equal(AgentRuntimeStatus.Failed, latest.Status);
        Assert.Contains("exited during startup", latest.Reason);
        Assert.Contains("manifest version", latest.LogExcerpt);
    }

    [Fact]
    public async Task AlwaysOnReconciliation_SuppressesImmediatelyForTerminalPackageBuildFailure()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        installation.Schedule.NextTickAt = null;
        installation.PackageVersion!.Status = AgentPackageVersionStatus.Failed;
        var failedBuild = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = installation.PackageVersion.Id,
            PackageVersion = installation.PackageVersion,
            Attempt = 3,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FailureMessage = "Source fetch failed after automatic retries."
        };
        failedBuild.TransitionTo(AgentBuildStatus.Cloning, DateTimeOffset.UtcNow.AddSeconds(-30));
        failedBuild.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow.AddSeconds(-20));
        db.AgentBuildJobs.Add(failedBuild);
        await db.SaveChangesAsync();
        var manager = CreateManager(db, new FakeRunner());

        Assert.Equal(1, await manager.EnsureAlwaysOnRuntimesAsync());
        Assert.Equal(1, await manager.ReconcileAsync());

        Assert.Equal(3, installation.Schedule.ConsecutiveStartupFailures);
        Assert.NotNull(installation.Schedule.AutomaticStartSuppressedAt);
        Assert.Null(installation.Schedule.NextTickAt);
        Assert.Equal(0, await manager.EnsureAlwaysOnRuntimesAsync());
        Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task McpSession_ResetsAlwaysOnStartupFailures()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        installation.Schedule.NextTickAt = null;
        installation.Schedule.ConsecutiveStartupFailures = 2;
        await db.SaveChangesAsync();
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        await manager.EnsureAlwaysOnRuntimesAsync();
        await manager.ReconcileAsync();
        var request = Assert.Single(containers.Starts);
        await new AgentRuntimeSignalService(db).RecordMcpSessionEstablishedAsync(
            request.RuntimeInstanceId,
            request.TickId,
            request.InstallationId,
            request.WorkloadToken);

        Assert.Equal(0, installation.Schedule.ConsecutiveStartupFailures);
        Assert.Null(installation.Schedule.AutomaticStartSuppressedAt);
    }

    [Fact]
    public async Task McpSession_ReconnectForSameRunningRuntime_IsAccepted()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);
        await new AgentInteractiveRuntimeService(db, manager).EnsureReadyAsync(installation.Id);
        var request = Assert.Single(containers.Starts);
        var signals = new AgentRuntimeSignalService(db);

        await signals.RecordMcpSessionEstablishedAsync(
            request.RuntimeInstanceId,
            request.TickId,
            request.InstallationId,
            request.WorkloadToken);
        await signals.RecordMcpSessionEstablishedAsync(
            request.RuntimeInstanceId,
            request.TickId,
            request.InstallationId,
            request.WorkloadToken);

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.Running, runtime.Status);
        Assert.Single(await db.AgentRuntimeEvents.Where(x => x.Status == AgentRuntimeStatus.Running).ToListAsync());
    }

    [Fact]
    public async Task McpSession_ReconnectStillValidatesWorkloadToken()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);
        await new AgentInteractiveRuntimeService(db, manager).EnsureReadyAsync(installation.Id);
        var request = Assert.Single(containers.Starts);
        var signals = new AgentRuntimeSignalService(db);
        await signals.RecordMcpSessionEstablishedAsync(
            request.RuntimeInstanceId,
            request.TickId,
            request.InstallationId,
            request.WorkloadToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            signals.RecordMcpSessionEstablishedAsync(
                request.RuntimeInstanceId,
                request.TickId,
                request.InstallationId,
                "invalid-token"));

        Assert.Equal("The runtime workload token is invalid.", exception.Message);
    }

    [Fact]
    public async Task DuePeriodicSchedule_StartsOneRuntimeAndSchedulesNextTick()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db);
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        Assert.Equal(1, await manager.ProcessDueSchedulesAsync());
        Assert.Equal(1, await manager.ReconcileAsync());

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, runtime.Status);
        Assert.Single(containers.Starts);
        Assert.True(installation.Schedule!.NextTickAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SkipOverlap_RecordsSkippedTickWithoutStartingSecondContainer()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db);
        db.AgentRuntimeInstances.Add(RunningInstance(installation.Id));
        await db.SaveChangesAsync();
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        await manager.ProcessDueSchedulesAsync();
        await manager.ReconcileAsync();

        Assert.Contains(await db.AgentRuntimeInstances.ToListAsync(), x => x.Status == AgentRuntimeStatus.Skipped);
        Assert.Empty(containers.Starts);
    }

    [Fact]
    public async Task AlwaysOnInitialTick_DoesNotRecordSkippedRuntimeWhenReconciliationAlreadyQueuedOne()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        db.AgentRuntimeInstances.Add(RunningInstance(installation.Id));
        await db.SaveChangesAsync();
        var manager = CreateManager(db, new FakeRunner());

        await manager.ProcessDueSchedulesAsync();

        var runtime = Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
        Assert.Equal(AgentRuntimeStatus.Running, runtime.Status);
        Assert.Null(installation.Schedule.NextTickAt);
    }

    [Fact]
    public async Task ConcurrencyLimit_LeavesRuntimeQueued()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, globalLimit: 1);
        var other = await SeedAsync(db, businessId: "business-2", due: false);
        db.AgentRuntimeInstances.Add(RunningInstance(other.Id));
        await db.SaveChangesAsync();
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        await manager.ProcessDueSchedulesAsync();
        await manager.ReconcileAsync();

        var queued = Assert.Single(
            await db.AgentRuntimeInstances
                .Where(x => x.AgentInstallationId == installation.Id && x.Status == AgentRuntimeStatus.Queued)
                .ToListAsync());
        Assert.Contains("global 1/1", queued.Reason);
        Assert.Empty(containers.Starts);
    }

    [Fact]
    public async Task ApprovedPackageBuild_KeepsRuntimeQueuedUntilBuildSucceeds()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db);
        var package = installation.PackageVersion!;
        package.Status = AgentPackageVersionStatus.Approved;
        package.PackagePath = null;
        var build = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            Attempt = 1,
            QueuedAt = DateTimeOffset.UtcNow
        };
        package.BuildJobs.Add(build);
        db.AgentBuildJobs.Add(build);
        await db.SaveChangesAsync();
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        await manager.ProcessDueSchedulesAsync();
        await manager.ReconcileAsync();

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.Queued, runtime.Status);
        Assert.Empty(containers.Starts);

        build.TransitionTo(AgentBuildStatus.Cloning, DateTimeOffset.UtcNow);
        build.TransitionTo(AgentBuildStatus.Building, DateTimeOffset.UtcNow);
        build.TransitionTo(AgentBuildStatus.Succeeded, DateTimeOffset.UtcNow);
        package.Status = AgentPackageVersionStatus.Built;
        package.PackagePath = "C:\\packages\\agent";
        await db.SaveChangesAsync();

        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, runtime.Status);
        Assert.Single(containers.Starts);
    }

    [Fact]
    public async Task FailedPackageBuild_FailsRuntimeInsteadOfReportingPolicyDenied()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db);
        var package = installation.PackageVersion!;
        package.Status = AgentPackageVersionStatus.Failed;
        package.PackagePath = null;
        var build = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            Attempt = 1,
            QueuedAt = DateTimeOffset.UtcNow,
            FailureMessage = "Compilation failed."
        };
        build.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        package.BuildJobs.Add(build);
        db.AgentBuildJobs.Add(build);
        await db.SaveChangesAsync();
        var manager = CreateManager(db, new FakeRunner());

        await manager.ProcessDueSchedulesAsync();
        await manager.ReconcileAsync();

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.Failed, runtime.Status);
        Assert.Contains("Compilation failed", runtime.Reason);
    }

    [Fact]
    public async Task FailedVmStart_FailsClosedWithoutNetworkFallback()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var containers = new FakeRunner
        {
            StartException = new AgentWorkloadException("RuntimeHost could not start the VM."),
            InspectStatus = null
        };
        var manager = CreateManager(db, containers);

        await manager.ProcessDueSchedulesAsync();
        await manager.ReconcileAsync();

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.StartFailed, runtime.Status);
        Assert.Null(runtime.ProviderInstanceId);
        Assert.Empty(containers.Removes);
    }

    [Fact]
    public async Task CompletionSignal_StopsContainerAndCompletesRuntime()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);
        await manager.ProcessDueSchedulesAsync();
        await manager.ReconcileAsync();
        var request = Assert.Single(containers.Starts);
        var signals = new AgentRuntimeSignalService(db);
        await signals.RecordMcpSessionEstablishedAsync(request.RuntimeInstanceId, request.TickId, request.InstallationId, request.WorkloadToken);
        await signals.RecordCompletionAsync(request.RuntimeInstanceId, request.TickId, request.InstallationId, "{\"succeeded\":true}");

        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Completed, (await db.AgentRuntimeInstances.SingleAsync()).Status);
        Assert.Single(containers.Stops);
        Assert.Single(containers.Removes);
    }

    [Fact]
    public async Task ExpiredRunningRuntime_IsStoppedAsTimedOut()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var runtime = RunningInstance(installation.Id);
        runtime.ProviderInstanceId = "container-id";
        runtime.IsolationProviderId = "test-vm";
        runtime.RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var containers = new FakeRunner();

        await CreateManager(db, containers).ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.RuntimeTimedOut, runtime.Status);
        Assert.Single(containers.Stops);
    }

    [Fact]
    public async Task SlowVmBoot_DoesNotConsumeMcpSessionEstablishmentWindow()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            BrokerTokenHash = new string('0', 64),
            RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5),
            IsolationProviderId = "test-vm",
            ProviderInstanceId = "slow-boot-vm"
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow.AddMinutes(-2));
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var containers = new FakeRunner();

        Assert.Equal(0, await CreateManager(db, containers).ReconcileAsync());

        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, runtime.Status);
        Assert.NotNull(runtime.McpSessionWaitingAt);
        Assert.Empty(containers.Stops);
        Assert.Empty(containers.Removes);
    }

    [Fact]
    public async Task ExpiredMcpSessionWindow_RetainsGuestLogsBeforeDestroyingVm()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            BrokerTokenHash = new string('0', 64),
            RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5),
            IsolationProviderId = "test-vm",
            ProviderInstanceId = "unresponsive-vm"
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow.AddMinutes(-2));
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow.AddSeconds(-31));
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var containers = new FakeRunner { Logs = "Chief of Staff startup diagnostics" };

        Assert.Equal(1, await CreateManager(db, containers).ReconcileAsync());

        Assert.Equal(AgentRuntimeStatus.McpSessionTimedOut, runtime.Status);
        Assert.Null(runtime.McpSessionWaitingAt);
        Assert.Equal("Chief of Staff startup diagnostics", runtime.LogExcerpt);
        Assert.Equal("unresponsive-vm", Assert.Single(containers.Stops));
        Assert.Equal("unresponsive-vm", Assert.Single(containers.Removes));
    }

    [Fact]
    public async Task InterruptedStoppingRuntime_IsRecoveredAndReleasesInstallationSlot()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var stoppedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var runtime = RunningInstance(installation.Id);
        runtime.ProviderInstanceId = "missing-container";
        runtime.IsolationProviderId = "test-vm";
        runtime.TransitionTo(AgentRuntimeStatus.Stopping, stoppedAt, "MCP session establishment timed out.");
        runtime.Events.Add(new AgentRuntimeEvent
        {
            Id = Guid.NewGuid(),
            AgentRuntimeInstanceId = runtime.Id,
            Status = AgentRuntimeStatus.Stopping,
            Reason = runtime.Reason,
            OccurredAt = stoppedAt
        });
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var containers = new FakeRunner { InspectStatus = null };
        var manager = CreateManager(db, containers);

        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Failed, runtime.Status);
        Assert.Null(runtime.ProviderInstanceId);
        Assert.Contains("fresh attempt", runtime.Reason);
        Assert.True(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Retry chat.", interactive: true));
    }

    [Fact]
    public async Task InterruptedStartingRuntimeWithoutContainer_FailsAndReleasesInstallationSlot()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = startedAt
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, startedAt, "Starting runtime container.");
        runtime.Events.Add(new AgentRuntimeEvent
        {
            Id = Guid.NewGuid(),
            AgentRuntimeInstanceId = runtime.Id,
            Status = AgentRuntimeStatus.Starting,
            Reason = runtime.Reason,
            OccurredAt = startedAt
        });
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var containers = new FakeRunner { InspectStatus = null };
        var manager = CreateManager(db, containers);

        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.StartFailed, runtime.Status);
        Assert.Contains("interrupted", runtime.Reason);
        Assert.True(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Retry chat.", interactive: true));
    }

    [Fact]
    public async Task InteractiveRuntime_RefreshesIdleDeadlineAndStopsAfterExpiry()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);
        await manager.EnsureRuntimeQueuedAsync(installation.Id, "Interactive request.", interactive: true);
        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        runtime.IdleDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.False(await manager.EnsureRuntimeQueuedAsync(installation.Id, "Interactive reuse.", interactive: true));
        Assert.True(runtime.IdleDeadlineAt > DateTimeOffset.UtcNow);

        runtime.ProviderInstanceId = "container-id";
        runtime.IsolationProviderId = "test-vm";
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
        runtime.IdleDeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Cancelled, runtime.Status);
        Assert.Single(containers.Stops);
    }

    [Fact]
    public async Task RestartRuntime_StopsExistingContainerAndQueuesFreshInteractiveInstance()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        var current = RunningInstance(installation.Id);
        current.ProviderInstanceId = "old-container";
        current.IsolationProviderId = "test-vm";
        current.AgentInstallation = installation;
        db.AgentRuntimeInstances.Add(current);
        await db.SaveChangesAsync();
        var containers = new FakeRunner();
        var manager = CreateManager(db, containers);

        var queued = await manager.RestartRuntimeAsync(
            installation.Id,
            "Organization assignment changed.",
            interactive: true);

        Assert.True(queued);
        Assert.Equal(AgentRuntimeStatus.Cancelled, current.Status);
        Assert.Equal("old-container", Assert.Single(containers.Stops));
        var replacement = Assert.Single(
            await db.AgentRuntimeInstances.Where(x => x.Status == AgentRuntimeStatus.Queued).ToListAsync());
        Assert.True(replacement.IsInteractive);
    }

    [Fact]
    public async Task AlwaysOnInteractiveRuntime_HasNoIdleDeadline()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.AlwaysOn;
        await db.SaveChangesAsync();

        await CreateManager(db, new FakeRunner()).EnsureRuntimeQueuedAsync(
            installation.Id,
            "Interactive request.",
            interactive: true);

        var runtime = await db.AgentRuntimeInstances.SingleAsync();
        Assert.True(runtime.IsInteractive);
        Assert.NotNull(runtime.LastInteractiveActivityAt);
        Assert.Null(runtime.IdleDeadlineAt);
    }

    [Fact]
    public void RuntimeModel_HasUniqueActiveInstallationIndex()
    {
        using var db = CreateDb();

        var index = db.Model.FindEntityType(typeof(AgentRuntimeInstance))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "UX_AgentRuntimeInstances_ActiveInstallation");

        Assert.True(index.IsUnique);
        Assert.Contains("'Queued'", index.GetFilter());
        Assert.Contains("'Stopping'", index.GetFilter());
    }

    private static AgentRuntimeManager CreateManager(CSweetDbContext db, FakeRunner runner)
    {
        runner.Db = db;
        return new(db, runner, new StaticGuestImageRegistry(), new TestAuditEventWriter(), Options.Create(new AgentRuntimeManagerOptions
        {
            RuntimeGuestImageVersion = "1.0",
            RuntimeGuestImageDigest = "sha256:" + new string('d', 64)
        }), NullLogger<AgentRuntimeManager>.Instance);
    }

    private sealed class StaticGuestImageRegistry : IGuestImageRegistry
    {
        public Task<GuestImageReference> ResolveAsync(
            GuestImageResolutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GuestImageReference(
                request.LogicalImageId,
                string.IsNullOrWhiteSpace(request.Version) ? "1.0" : request.Version,
                "sha256:" + new string('d', 64),
                request.OperatingSystem,
                request.Architecture));
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<AgentInstallation> SeedAsync(CSweetDbContext db, string businessId = "business-1", bool due = true, int globalLimit = 10)
    {
        if (!await db.AgentRuntimeGlobalSettings.AnyAsync())
            db.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings { Id = Guid.NewGuid(), EnableImportedAgents = true, GlobalMaxActiveWorkloads = globalLimit, PerBusinessMaxActiveWorkloads = 5, PerInstallationMaxActiveWorkloads = 1, DefaultWorkloadProcessLimit = 100, McpSessionTimeoutSeconds = 30, WorkloadStopGraceSeconds = 1, RemoveWorkloadsAfterCompletion = true });
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), CommitSha = new string('a', 40), ManifestDigest = new string('b', 64), ManifestJson = "{}", AgentId = "com.example.agent", AgentName = "Agent", Version = "1.0.0", PublisherId = "example", PublisherName = "Example", RuntimeType = "dotnet-project", ProjectPath = "src/Example.Agent.csproj", WarningsJson = "[]", Status = AgentPackageVersionStatus.Built, PackagePath = "artifact:agent", PackageDigest = new string('c', 64), ArtifactSignature = "test-signature", ImportedAt = DateTimeOffset.UtcNow };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = package.Id, BusinessId = businessId, IsEnabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, PackageVersion = package };
        installation.Grant = new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, MemoryMb = 512, CpuPercent = 50, MaxRuntimeSeconds = 60, ApprovedAt = DateTimeOffset.UtcNow };
        installation.Schedule = new AgentSchedule { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, ActivationMode = ActivationMode.Periodic, TickFrequencySeconds = 300, NextTickAt = due ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddHours(1), MaxRuntimeSeconds = 60, OverlapPolicy = OverlapPolicy.Skip, IsEnabled = true };
        db.AgentInstallations.Add(installation);
        await db.SaveChangesAsync();
        return installation;
    }

    private static AgentRuntimeInstance RunningInstance(Guid installationId)
    {
        var instance = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installationId, QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), BrokerTokenHash = new string('0', 64), RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5), IsolationProviderId = "test-vm" };
        instance.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow.AddMinutes(-1));
        instance.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow.AddMinutes(-1));
        instance.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        return instance;
    }

    private sealed record StartedWorkload(
        RuntimeWorkloadSpec Workload,
        Guid RuntimeInstanceId,
        Guid TickId,
        Guid InstallationId,
        string WorkloadToken);

    private sealed class FakeRunner : IAgentWorkloadRunner
    {
        public CSweetDbContext? Db { get; set; }
        public IsolationWorkloadStatus? InspectStatus { get; init; } = new(
            new IsolationWorkloadHandle("test-vm", Guid.NewGuid(), "vm-1", IsolationWorkloadKind.Runtime),
            IsolationWorkloadState.Running, IsolationTerminationReason.None, null, null, null, null, null);
        public Exception? StartException { get; init; }
        public string Logs { get; init; } = string.Empty;
        public List<StartedWorkload> Starts { get; } = [];
        public List<string> Stops { get; } = [];
        public List<string> Removes { get; } = [];
        public async Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpec workload, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default)
        {
            if (StartException is not null) throw StartException;
            var runtime = await (Db ?? throw new InvalidOperationException()).AgentRuntimeInstances
                .SingleAsync(x => x.Id == workload.WorkloadId, cancellationToken);
            Starts.Add(new StartedWorkload(
                workload, runtime.Id, runtime.TickId, runtime.AgentInstallationId, workload.BrokerLease.BootToken));
            return new IsolationWorkloadHandle("test-vm", workload.WorkloadId, "vm-1", IsolationWorkloadKind.Runtime);
        }
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) { Stops.Add(handle.ProviderInstanceId); return Task.CompletedTask; }
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(InspectStatus is null ? null : InspectStatus with { Handle = handle });
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Removes.Add(handle.ProviderInstanceId); return Task.CompletedTask; }
        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult(Logs);
    }
}
