using System.Security.Cryptography;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class AutomaticOfficeUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreparationDoesNotDrainOrStopJobsAndConsumesTheHandoffOnce()
    {
        await using var f = await Fixture.Create();
        var assignment = await f.AddJob();
        var created = await f.Start();
        var prepared = await f.Begin(created);
        Assert.True(prepared.Succeeded);
        Assert.Equal("0.13.0", prepared.TargetVersion);
        Assert.Equal(ExecutionNodeStatus.Ready, f.Office.Status);
        Assert.Equal(ExecutionAssignmentStatus.Running, assignment.Status);
        Assert.False((await f.Begin(created)).Succeeded);
        var stored = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        Assert.NotEqual(prepared.Receipt, stored.PreparationReceiptHash);
        Assert.True(stored.ExpiresAt > Now.AddMinutes(5));
    }

    [Fact]
    public async Task InstallerWaitsForPhysicalStopEvidenceEvenAfterCancellationAndLeaseExpiry()
    {
        await using var f = await Fixture.Create();
        var assignment = await f.AddJob();
        var prepared = await f.Begin(await f.Start());
        var waiting = await f.Progress(prepared, true);
        Assert.Equal("stopping", waiting.State);
        Assert.Null(waiting.LaunchUri);
        Assert.Equal(ExecutionNodeStatus.Draining, f.Office.Status);
        Assert.Equal(ExecutionAssignmentStatus.Cancelled, assignment.Status);
        Assert.Equal(2, assignment.FencingEpoch);
        Assert.Null(assignment.LeaseExpiresAt);
        Assert.False(await f.Fleet.CanUpgradeOfficeAsync(f.Office.Id));
        Assert.Equal("stopping", (await f.Progress(prepared, true)).State);
        Assert.Single(await f.Db.Set<OfficeUpdateJob>().ToListAsync());
        var attempt = await f.Db.ExecutionAssignmentAttempts.SingleAsync();
        attempt.StoppedAt = Now; await f.Db.SaveChangesAsync();
        var install = await f.Progress(prepared, true);
        Assert.Equal("installing", install.State);
        Assert.NotNull(install.LaunchUri);
        Assert.True(await f.Fleet.CanUpgradeOfficeAsync(f.Office.Id));
        Assert.False((await f.Fleet.CancelOfficeUpdateAsync(f.SessionId, f.UserId)).Succeeded);
    }

    [Fact]
    public async Task DownloadFailureLeavesCurrentWorkRunningAndCannotIssueInstallationHandoff()
    {
        await using var f = await Fixture.Create();
        var job = await f.AddJob();
        var prepared = await f.Begin(await f.Start());
        var failure = await f.Fleet.AdvanceOfficeUpdatePreparationAsync(f.Request(prepared) with { ErrorCode = "package_preparation_failed" });
        Assert.False(failure.Succeeded);
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(ExecutionNodeStatus.Ready, f.Office.Status);
        Assert.Equal(ExecutionAssignmentStatus.Running, job.Status);
        Assert.Null((await f.Progress(prepared, true)).LaunchUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedReconnectRestoresThePreviousWorkSettingWithoutASecondClick(bool paused)
    {
        await using var f = await Fixture.Create(paused);
        var prepared = await f.Begin(await f.Start());
        await f.Progress(prepared, true);
        var session = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        session.Status = LocalOfficeSetupSessionStatus.Connected;
        session.CompletedAt = Now.AddSeconds(-2);
        session.AdministratorApprovalRequestedAt = Now;
        f.Office.NodeVersion = "0.13.0";
        f.Office.LastHeartbeatAt = Now;
        await f.Db.SaveChangesAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(LocalOfficeSetupSessionStatus.Ready, session.Status);
        Assert.NotNull(session.WorkRestoredAt);
        Assert.Equal(paused ? ExecutionNodeStatus.Draining : ExecutionNodeStatus.Ready, f.Office.Status);
        Assert.Equal(paused, f.Office.DrainingAt is not null);
        var events = await f.Db.ApplicationRealtimeOutbox.CountAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(events, await f.Db.ApplicationRealtimeOutbox.CountAsync());
    }

    [Theory]
    [InlineData("0.12.0", false, true)]
    [InlineData("0.13.0", true, true)]
    [InlineData("0.13.0", false, false)]
    public async Task WrongVersionStaleHeartbeatOrUnhealthyProviderCannotCompleteUpdate(string reported, bool stale, bool healthy)
    {
        await using var f = await Fixture.Create();
        var prepared = await f.Begin(await f.Start()); await f.Progress(prepared, true);
        var session = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        session.Status = LocalOfficeSetupSessionStatus.Connected; session.CompletedAt = Now.AddMinutes(-2);
        f.Office.NodeVersion = reported; f.Office.LastHeartbeatAt = stale ? Now.AddMinutes(-1) : Now;
        f.Office.Providers.Single().IsAvailable = healthy;
        await f.Db.SaveChangesAsync(); await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(LocalOfficeSetupSessionStatus.Connected, session.Status);
        Assert.Null(session.WorkRestoredAt); Assert.NotNull(f.Office.DrainingAt);
    }

    [Theory]
    [InlineData(OfficeWorkPauseReason.Maintenance, true)]
    [InlineData(OfficeWorkPauseReason.User, false)]
    [InlineData(null, false)]
    public async Task UpdateResumesAMaintenancePauseAndPreservesADeliberateOrUnknownPause(OfficeWorkPauseReason? reason, bool shouldResume)
    {
        await using var f = await Fixture.Create(paused: true);
        f.Office.WorkPauseReason = reason;
        await f.Db.SaveChangesAsync();
        var created = await f.Start();
        Assert.Equal(shouldResume, created.Session!.ResumeAfterUpdate);
        var prep = await f.Begin(created);
        await f.Progress(prep, true);
        var session = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        session.Status = LocalOfficeSetupSessionStatus.Connected;
        session.CompletedAt = Now.AddSeconds(-1);
        f.Office.NodeVersion = "0.13.0";
        await f.Db.SaveChangesAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(shouldResume ? ExecutionNodeStatus.Ready : ExecutionNodeStatus.Draining, f.Office.Status);
        Assert.Equal(shouldResume ? null : reason, f.Office.WorkPauseReason);
    }

    [Fact]
    public async Task CompletedUpdateMessageReflectsAResumeAfterTheUpdate()
    {
        await using var f = await Fixture.Create(paused: true);
        var prep = await f.Begin(await f.Start());
        await f.Progress(prep, true);
        var session = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        session.Status = LocalOfficeSetupSessionStatus.Connected;
        session.CompletedAt = Now.AddSeconds(-1);
        f.Office.NodeVersion = "0.13.0";
        await f.Db.SaveChangesAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Contains("currently paused", (await f.Fleet.GetLocalSetupSessionAsync(f.SessionId, f.UserId)).Session!.Message);
        f.Office.Status = ExecutionNodeStatus.Ready;
        f.Office.DrainingAt = null;
        f.Office.WorkPauseReason = null;
        await f.Db.SaveChangesAsync();
        Assert.Contains("ready for work", (await f.Fleet.GetLocalSetupSessionAsync(f.SessionId, f.UserId)).Session!.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PausedCapacityIsReportedOnlyWhenItWouldOtherwiseBeHealthy(bool healthy)
    {
        await using var f = await Fixture.Create(paused: true);
        f.Office.Providers.Single().IsAvailable = healthy;
        await f.Db.SaveChangesAsync();
        var capacity = await f.Fleet.GetOnboardingStatusAsync();
        Assert.False(capacity.IsReady);
        Assert.Equal(healthy, capacity.Checks.Any(x => x.Key == "work-paused" && x.Status == "passed"));
        Assert.Equal("user", capacity.Nodes.Single().WorkPauseReason);
    }

    [Fact]
    public async Task CancellationWaitsForStopAndKeepsOriginalOfficeVersion()
    {
        await using var f = await Fixture.Create();
        await f.AddJob(); var prepared = await f.Begin(await f.Start()); await f.Progress(prepared, true);
        Assert.True((await f.Fleet.CancelOfficeUpdateAsync(f.SessionId, f.UserId)).Succeeded);
        Assert.False((await f.Progress(prepared, true)).Succeeded);
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(ExecutionNodeStatus.Draining, f.Office.Status);
        var attempt = await f.Db.ExecutionAssignmentAttempts.SingleAsync(); attempt.StoppedAt = Now;
        await f.Db.SaveChangesAsync(); await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(ExecutionNodeStatus.Ready, f.Office.Status); Assert.Equal("0.12.0", f.Office.NodeVersion);
        Assert.Equal(LocalOfficeSetupSessionStatus.Revoked, (await f.Db.LocalOfficeSetupSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task ForgedReceiptsAndWrongMachineCannotStopJobs()
    {
        await using var f = await Fixture.Create();
        var job = await f.AddJob(); var prepared = await f.Begin(await f.Start());
        Assert.False((await f.Fleet.AdvanceOfficeUpdatePreparationAsync(f.Request(prepared) with { MachineName = "another-computer", PackagePrepared = true })).Succeeded);
        Assert.False((await f.Fleet.AdvanceOfficeUpdatePreparationAsync(f.Request(prepared) with { Receipt = "forged", PackagePrepared = true })).Succeeded);
        Assert.Equal(ExecutionAssignmentStatus.Running, job.Status); Assert.Null(f.Office.DrainingAt);
    }

    [Fact]
    public async Task PreparationAndStopStateHaveNotificationOutboxRecordsInTheirSave()
    {
        await using var f = await Fixture.Create();
        await f.AddJob(); var prepared = await f.Begin(await f.Start()); await f.Progress(prepared, true);
        var events = await f.Db.ApplicationRealtimeOutbox.ToListAsync();
        Assert.Contains(events, x => x.Subject == $"offices/{f.Office.Id:D}/update" && x.DataJson.Contains("stopping"));
        Assert.All(events, x => Assert.Equal(f.UserId, x.RecipientApplicationUserId));
        Assert.DoesNotContain(events, x => x.DataJson.Contains(prepared.Receipt!));
    }

    [Fact]
    public async Task AnAlreadyPausedIdleOfficeStillCannotRedeemBeforePackagePreparation()
    {
        await using var f = await Fixture.Create(paused: true);
        var created = await f.Start();
        var secret = Uri.UnescapeDataString(new Uri(created.Session!.LaunchUri!).Fragment["#handoff=".Length..]);
        var result = await f.Fleet.RedeemLocalSetupSessionAsync(new(secret, f.Office.MachineName, "windows", "x64", "0.13.0"));
        Assert.False(result.Succeeded); Assert.Equal("upgrade_not_safe", result.ErrorCode);
        Assert.Null((await f.Db.LocalOfficeSetupSessions.SingleAsync()).RedeemedAt);
    }

    [Fact]
    public async Task FullInstallerHandoffFinishesAndResumesFromTheDurableWorker()
    {
        await using var f = await Fixture.Create();
        var prep = await f.Begin(await f.Start()); var install = await f.Progress(prep, true);
        var secret = Uri.UnescapeDataString(new Uri(install.LaunchUri!).Fragment["#handoff=".Length..]);
        var redeemed = await f.Fleet.RedeemLocalSetupSessionAsync(new(secret, f.Office.MachineName, "windows", "x64", "0.13.0"));
        Assert.True(redeemed.Succeeded);
        Assert.False((await f.Fleet.RedeemLocalSetupSessionAsync(new(secret, f.Office.MachineName, "windows", "x64", "0.13.0"))).Succeeded);
        Assert.True(await f.Fleet.ReportLocalSetupResultAsync(new(f.SessionId, redeemed.SetupReceipt!, "office_upgrade_completed", f.Office.MachineName, "windows", "x64")));
        f.Office.NodeVersion = "0.13.0"; f.Office.LastHeartbeatAt = Now.AddSeconds(1); await f.Db.SaveChangesAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        var result = await f.Fleet.GetLocalSetupSessionAsync(f.SessionId, f.UserId);
        Assert.Equal("ready", result.Session!.State); Assert.Null(f.Office.DrainingAt);
        Assert.Equal(ExecutionNodeStatus.Ready, f.Office.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryKeepsTheOriginalPausePolicyAndRecoveryIntent(bool originallyPaused)
    {
        await using var f = await Fixture.Create(originallyPaused);
        await f.AddJob(); var prep = await f.Begin(await f.Start()); await f.Progress(prep, true);
        var old = await f.Db.LocalOfficeSetupSessions.SingleAsync(); old.Status = LocalOfficeSetupSessionStatus.Failed;
        old.ErrorCode = "office_setup_failed"; await f.Db.SaveChangesAsync();
        var retried = await f.Start();
        Assert.Equal(!originallyPaused, retried.Session!.ResumeAfterUpdate);
        Assert.Equal("superseded", old.UpdatePhase);
        Assert.Single(await f.Db.Set<OfficeUpdateJob>().Where(x => x.SessionId == retried.Session.Id).ToArrayAsync());
    }

    [Fact]
    public async Task CancellingDuringDownloadLeavesRunningJobsAlone()
    {
        await using var f = await Fixture.Create();
        var job = await f.AddJob();
        await f.Begin(await f.Start());
        Assert.True((await f.Fleet.CancelOfficeUpdateAsync(f.SessionId, f.UserId)).Succeeded);
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        var session = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        Assert.Equal(LocalOfficeSetupSessionStatus.Revoked, session.Status);
        Assert.NotNull(session.WorkRestoredAt);
        Assert.Equal(ExecutionAssignmentStatus.Running, job.Status);
        Assert.Equal(ExecutionNodeStatus.Ready, f.Office.Status);
    }

    [Fact]
    public async Task AJobAlreadyStoppingBeforeTheUpdateIsNotQueuedForRecovery()
    {
        await using var f = await Fixture.Create();
        var job = await f.AddJob();
        job.Status = ExecutionAssignmentStatus.Stopping;
        await f.Db.SaveChangesAsync();
        var prep = await f.Begin(await f.Start());
        Assert.Equal("stopping", (await f.Progress(prep, true)).State);
        Assert.False((await f.Db.Set<OfficeUpdateJob>().SingleAsync()).NeedsRecovery);
        Assert.Null((await f.Db.ExecutionAssignmentAttempts.SingleAsync()).StoppedAt);
    }

    [Fact]
    public async Task InstallerCannotRedeemAPayloadOtherThanTheReviewedVersion()
    {
        await using var f = await Fixture.Create();
        var prep = await f.Begin(await f.Start());
        var install = await f.Progress(prep, true);
        var secret = Uri.UnescapeDataString(new Uri(install.LaunchUri!).Fragment["#handoff=".Length..]);
        var result = await f.Fleet.RedeemLocalSetupSessionAsync(new(secret, f.Office.MachineName, "windows", "x64", "0.14.0"));
        Assert.False(result.Succeeded);
        Assert.Equal("update_version_mismatch", result.ErrorCode);
        Assert.Null((await f.Db.LocalOfficeSetupSessions.SingleAsync()).RedeemedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedRetryDownloadRetainsTheOriginalRecoveryIntent(bool originallyPaused)
    {
        await using var f = await Fixture.Create(originallyPaused);
        await f.AddJob();
        var first = await f.Begin(await f.Start());
        await f.Progress(first, true);
        var old = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        old.Status = LocalOfficeSetupSessionStatus.Failed;
        await f.Db.SaveChangesAsync();
        var retry = await f.Begin(await f.Start());
        await f.Fleet.AdvanceOfficeUpdatePreparationAsync(f.Request(retry) with { ErrorCode = "package_preparation_failed" });
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        var failedRetry = await f.Db.LocalOfficeSetupSessions.SingleAsync(x => x.Id == f.SessionId);
        Assert.Null(failedRetry.WorkRestoredAt);
        Assert.NotNull(failedRetry.StopStartedAt);
        Assert.Equal(ExecutionNodeStatus.Draining, f.Office.Status);
        var next = await f.Start();
        Assert.Equal(!originallyPaused, next.Session!.ResumeAfterUpdate);
        Assert.Single(await f.Db.Set<OfficeUpdateJob>().Where(x => x.SessionId == next.Session.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedConnectionRepairRestoresWorkWithoutClaimingTheUpdateSucceeded(bool originallyPaused)
    {
        await using var f = await Fixture.Create(originallyPaused);
        var prep = await f.Begin(await f.Start());
        await f.Progress(prep, true);
        var update = await f.Db.LocalOfficeSetupSessions.SingleAsync();
        update.Status = LocalOfficeSetupSessionStatus.Failed;
        f.Db.LocalOfficeSetupSessions.Add(new()
        {
            Id = Guid.NewGuid(), CreatedByUserId = f.UserId, UpgradeOfficeId = f.Office.Id,
            HandoffSecretHash = "separate-repair", RecoveryAction = "repair", CreatedAt = Now.AddSeconds(1), ExpiresAt = Now.AddHours(1),
            CompletedAt = Now.AddSeconds(2), Status = LocalOfficeSetupSessionStatus.Connected
        });
        f.Office.LastHeartbeatAt = Now.AddSeconds(3);
        await f.Db.SaveChangesAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.NotNull(update.WorkRestoredAt);
        Assert.Equal(LocalOfficeSetupSessionStatus.Failed, update.Status);
        Assert.Equal("0.12.0", f.Office.NodeVersion);
        Assert.Equal(originallyPaused ? ExecutionNodeStatus.Draining : ExecutionNodeStatus.Ready, f.Office.Status);
    }

    [Fact]
    public async Task PlannedRuntimeInterruptionPreservesPayloadAndDoesNotSpendAFailureRetry()
    {
        await using var f = await Fixture.Create();
        var installation = new AgentInstallation { Id = Guid.NewGuid(), IsEnabled = true };
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, TickId = Guid.NewGuid(), QueuedAt = Now };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, Now); runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, Now);
        runtime.TransitionTo(AgentRuntimeStatus.Running, Now);
        var assignment = await f.AddJob(); assignment.WorkloadKind = ExecutionWorkloadKind.Runtime; assignment.AgentRuntimeInstanceId = runtime.Id;
        var work = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, Status = AgentWorkStatus.Leased,
            AttemptCount = 3, MaximumAttempts = 3, DeadlineAt = Now.AddMinutes(1), ProtectedPayload = [1, 2, 3], CreatedAt = Now };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id,
            Attempt = 3, ClaimedAt = Now, LeaseExpiresAt = Now.AddSeconds(30) };
        f.Db.AddRange(installation, runtime, work, attempt); await f.Db.SaveChangesAsync();
        var prep = await f.Begin(await f.Start()); await f.Progress(prep, true);
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.True(attempt.LeaseExpiresAt > Now.AddMinutes(30));
        var execution = await f.Db.ExecutionAssignmentAttempts.SingleAsync(); execution.StoppedAt = Now;
        runtime.TransitionTo(AgentRuntimeStatus.Cancelled, Now);
        var session = await f.Db.LocalOfficeSetupSessions.SingleAsync(); session.Status = LocalOfficeSetupSessionStatus.Connected;
        session.CompletedAt = Now.AddSeconds(-1); f.Office.NodeVersion = "0.13.0";
        await f.Db.SaveChangesAsync();
        await f.Fleet.ReconcileOfficeUpdatesAsync();
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        Assert.Equal(1, work.MaintenanceInterruptionCount);
        Assert.True(work.AttemptCount - work.ResourceReplacementCount - work.MaintenanceInterruptionCount < work.MaximumAttempts);
        Assert.Equal(new byte[] { 1, 2, 3 }, work.ProtectedPayload);
        Assert.Equal("office-update-interrupted", attempt.Error); Assert.NotNull(attempt.FinishedAt);
        Assert.Equal(1, f.Runtimes.Queued);
        await f.Fleet.ReconcileOfficeUpdatesAsync(); Assert.Equal(1, f.Runtimes.Queued);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; }
        public ExecutionFleetService Fleet { get; }
        public ExecutionNode Office { get; private set; } = default!;
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid SessionId { get; private set; }
        public RuntimeManager Runtimes { get; } = new();
        private Fixture()
        {
            Db = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Fleet = new(Db, new TestAuditEventWriter(), new FixedClock(), Options.Create(new ExecutionFleetOptions
            { PublicLaunchEnabled = true, AllowUnpinnedDevelopmentImages = true }), appConfiguration:
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test", ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64) }).Build(),
                runtimeOptions: Options.Create(new AgentRuntimeManagerOptions { RequiredCertificationSuiteVersion = "test" }),
                services: new ServiceCollection().AddSingleton<IAgentRuntimeManager>(Runtimes).BuildServiceProvider());
        }
        public static async Task<Fixture> Create(bool paused = false)
        {
            var f = new Fixture(); await new SetupService(f.Db).EnsureSeededAsync(); await f.Fleet.EnsureDefaultPoolAsync();
            f.Office = new() { Id = Guid.NewGuid(), ExecutionPoolId = (await f.Db.ExecutionPools.FirstAsync()).Id,
                Name = "Studio", MachineName = "office-machine", OperatingSystem = "windows", Architecture = "x64", NodeVersion = "0.12.0",
                ApprovedAt = Now, Status = paused ? ExecutionNodeStatus.Draining : ExecutionNodeStatus.Ready, DrainingAt = paused ? Now : null,
                WorkPauseReason = paused ? OfficeWorkPauseReason.User : null,
                CertificateThumbprint = "ABCD", CertificateExpiresAt = Now.AddDays(1), LastHeartbeatAt = Now,
                AllocatableCpuCount = 4, AllocatableMemoryMb = 8192, AllocatableDiskMb = 20480, MaximumConcurrentWorkloads = 4 };
            f.Office.Providers.Add(new() { Id = Guid.NewGuid(), ProviderId = "test", IsAvailable = true, SupportsBuilderWorkloads = true,
                SupportsRuntimeWorkloads = true, CertificationSuiteVersion = "test", CertifiedAt = Now.AddDays(-1),
                GuestImageDigest = "sha256:" + new string('a', 64), CertificationEvidenceDigest = "sha256:" + new string('b', 64) });
            f.Db.ExecutionNodes.Add(f.Office); await f.Db.SaveChangesAsync(); return f;
        }
        public async Task<ExecutionWorkloadAssignment> AddJob()
        {
            var job = new ExecutionWorkloadAssignment { Id = Guid.NewGuid(), ExecutionNodeId = Office.Id, ExecutionPoolId = Office.ExecutionPoolId,
                WorkloadKind = ExecutionWorkloadKind.Builder, Status = ExecutionAssignmentStatus.Running, ProviderId = "test", StopEvidenceVersion = 1,
                AssignedAt = Now, LeaseExpiresAt = Now.AddSeconds(60) };
            Db.ExecutionWorkloadAssignments.Add(job); Db.ExecutionAssignmentAttempts.Add(new() { AssignmentId = job.Id, FencingEpoch = 1,
                ExecutionNodeId = Office.Id, ProviderId = "test", ProviderInstanceId = "vm", AssignedAt = Now });
            await Db.SaveChangesAsync(); return job;
        }
        public async Task<LocalOfficeSetupActionResponse> Start()
        {
            var result = await Fleet.CreateLocalSetupSessionAsync(new("custom", 4, 8192, 20480, Office.Id, AutomaticUpdate: true, TargetVersion: "0.13.0"), UserId);
            Assert.True(result.Succeeded, result.Message); SessionId = result.Session!.Id; return result;
        }
        public Task<OfficeUpdatePreparationResponse> Begin(LocalOfficeSetupActionResponse created)
        {
            var secret = Uri.UnescapeDataString(new Uri(created.Session!.LaunchUri!).Fragment["#handoff=".Length..]);
            return Fleet.BeginOfficeUpdatePreparationAsync(new(secret, Office.MachineName, "windows", "x64"));
        }
        public OfficeUpdatePreparationRequest Request(OfficeUpdatePreparationResponse prepared) => new(SessionId, prepared.Receipt!, Office.MachineName, "windows", "x64");
        public Task<OfficeUpdatePreparationResponse> Progress(OfficeUpdatePreparationResponse prepared, bool ready) =>
            Fleet.AdvanceOfficeUpdatePreparationAsync(Request(prepared) with { PackagePrepared = ready });
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RuntimeManager : IAgentRuntimeManager
    {
        public int Queued { get; private set; }
        public Task<bool> EnsureRuntimeQueuedAsync(Guid id, string reason, bool interactive = false, CancellationToken cancellationToken = default)
        { Queued++; return Task.FromResult(true); }
        public Task<bool> RestartRuntimeAsync(Guid id, string reason, bool interactive = false, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
