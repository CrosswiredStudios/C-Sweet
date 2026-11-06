using System.Reflection;
using System.Runtime.CompilerServices;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

/// <summary>
/// Exits from a local Office setup that cannot finish by itself: continuing after the Hyper-V restart,
/// "Start over", and expiry of a redeemed session whose elevated setup process died.
/// </summary>
public sealed class LocalOfficeSetupRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("restart-required", -10, -5, true)]   // booted after the request: resumable
    [InlineData("restart-required", -10, -15, false)] // booted before the request: still needs a restart
    [InlineData("running", -10, -5, false)]
    [InlineData("failed", -10, -5, false)]
    public void RestartResumeRequiresABootAfterTheRequest(string state, int observedMinutes, int bootMinutes, bool expected) =>
        Assert.Equal(expected, ExecutionFleetService.IsRestartResumeEligible(
            state, Now.AddMinutes(observedMinutes), Now.AddMinutes(bootMinutes), Now));

    [Fact]
    public void RestartResumeWindowIsBounded() =>
        Assert.False(ExecutionFleetService.IsRestartResumeEligible(
            "restart-required", Now - ExecutionFleetService.RestartResumeWindow - TimeSpan.FromMinutes(1),
            Now.AddMinutes(-1), Now));

    [Theory]
    [InlineData(null, null, -31, false, true)]                 // no progress, no owner, past the grace
    [InlineData(null, null, -29, false, false)]                // still within the grace
    [InlineData("running", -1, -120, true, false)]             // live owner still reporting
    [InlineData("failed", -45, -120, false, true)]             // dead owner, stale progress
    [InlineData("failed", -5, -120, false, false)]             // recent progress
    [InlineData("restart-required", -600, -620, false, false)] // waiting for the restart, inside the window
    public void AbandonedRedeemedSetupNeedsNoLiveOwnerAndNoRecentProgress(
        string? state, int? observedMinutes, int redeemedMinutes, bool ownerAlive, bool expected) =>
        Assert.Equal(expected, ExecutionFleetService.IsAbandonedRedeemedSetup(
            state, observedMinutes is { } minutes ? Now.AddMinutes(minutes) : null,
            Now.AddMinutes(redeemedMinutes), Now, ownerAlive));

    [Fact]
    public void RestartRequestExpiresAfterItsWindow() =>
        Assert.True(ExecutionFleetService.IsAbandonedRedeemedSetup(
            "restart-required", Now - ExecutionFleetService.RestartResumeWindow - TimeSpan.FromHours(1),
            Now.AddDays(-8), Now, ownerAlive: false));

    [Fact]
    public async Task ContinueAfterRestartMintsOneFreshHandoffAndRevokesTheStoppedRunsEnrollment()
    {
        await using var db = await CreateDbAsync();
        var audit = new TestAuditEventWriter();
        var fleet = CreateFleet(db, audit);
        var (session, enrollment) = await SeedRedeemedAsync(db);
        fleet.SetupProgressReader = (id, _) => Progress(id, "restart-required", Now.AddMinutes(-10));
        fleet.LastBootTimeReader = () => Now.AddMinutes(-5);
        var consumedHash = session.HandoffSecretHash;

        var resumed = await fleet.ContinueLocalSetupAfterRestartAsync(session.Id, session.CreatedByUserId);

        Assert.True(resumed.Succeeded, resumed.Message);
        Assert.Equal("created", resumed.Session?.State);
        var launchUri = new Uri(Assert.IsType<string>(resumed.Session?.LaunchUri));
        var handoff = Uri.UnescapeDataString(launchUri.Fragment["#handoff=".Length..]);
        var stored = await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(LocalOfficeSetupSessionStatus.Created, stored.Status);
        Assert.NotEqual(consumedHash, stored.HandoffSecretHash);
        Assert.Equal(Sha256(handoff), stored.HandoffSecretHash);
        Assert.Null(stored.ExecutionNodeEnrollmentId);
        Assert.Equal(ExecutionEnrollmentStatus.Revoked,
            (await db.ExecutionNodeEnrollments.AsNoTracking().SingleAsync(x => x.Id == enrollment.Id)).Status);
        Assert.Contains(audit.Events, x => x.EventType == "office.local-setup.restart-resumed");

        // Single use: the same request cannot mint a second handoff.
        var again = await fleet.ContinueLocalSetupAfterRestartAsync(session.Id, session.CreatedByUserId);
        Assert.False(again.Succeeded);
        Assert.Equal("resume_not_available", again.ErrorCode);
        Assert.Equal(stored.HandoffSecretHash,
            (await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).HandoffSecretHash);
    }

    [Fact]
    public async Task ContinueIsRefusedUntilWindowsHasRestarted()
    {
        await using var db = await CreateDbAsync();
        var fleet = CreateFleet(db, new TestAuditEventWriter());
        var (session, _) = await SeedRedeemedAsync(db);
        fleet.SetupProgressReader = (id, _) => Progress(id, "restart-required", Now.AddMinutes(-10));
        fleet.LastBootTimeReader = () => Now.AddDays(-1);

        var mapped = fleet.Map(session, null, null, windowsProgress: Progress(session.Id, "restart-required", Now.AddMinutes(-10)));
        Assert.False(mapped.CanContinueAfterRestart);
        Assert.Contains("Continue setup", mapped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("resume automatically", mapped.Message, StringComparison.OrdinalIgnoreCase);

        var result = await fleet.ContinueLocalSetupAfterRestartAsync(session.Id, session.CreatedByUserId);
        Assert.False(result.Succeeded);
        Assert.Equal("restart_not_detected", result.ErrorCode);
        Assert.Equal(LocalOfficeSetupSessionStatus.Redeemed,
            (await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task RestartedSessionOffersContinueSetup()
    {
        await using var db = await CreateDbAsync();
        var fleet = CreateFleet(db, new TestAuditEventWriter());
        var (session, _) = await SeedRedeemedAsync(db);
        fleet.LastBootTimeReader = () => Now.AddMinutes(-5);

        var mapped = fleet.Map(session, null, null, windowsProgress: Progress(session.Id, "restart-required", Now.AddMinutes(-10)));

        Assert.True(mapped.CanContinueAfterRestart);
        Assert.True(mapped.CanStartOver);
        Assert.Equal("Ready to continue setup", mapped.PhaseDisplayName);
    }

    [WindowsFact]
    public async Task ServerContinueStartsTheElevatedSetupExactlyOnce()
    {
        await using var db = await CreateDbAsync();
        var fleet = CreateFleet(db, new TestAuditEventWriter(), new ExecutionFleetOptions
        {
            PublicLaunchEnabled = true,
            WindowsDevelopmentLauncherScript = typeof(LocalOfficeSetupRecoveryTests).Assembly.Location
        });
        var (session, _) = await SeedRedeemedAsync(db, MachineBinding(Environment.MachineName, "windows",
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()));
        fleet.SetupProgressReader = (id, _) => Progress(id, "restart-required", Now.AddMinutes(-10));
        fleet.LastBootTimeReader = () => Now.AddMinutes(-5);
        var launches = new List<string>();
        fleet.DevelopmentSetupStarterOverride = (_, uri) =>
        {
            launches.Add(uri);
            return new ExecutionFleetService.DevelopmentLaunchResult(true, null, null);
        };

        var first = await fleet.ContinueLocalSetupAfterRestartAsync(session.Id, session.CreatedByUserId);
        var second = await fleet.ContinueLocalSetupAfterRestartAsync(session.Id, session.CreatedByUserId);

        Assert.True(first.Succeeded, first.Message);
        Assert.Single(launches);
        Assert.True(second.Succeeded);
        Assert.Equal("Office setup is already continuing.", second.Message);
        Assert.NotNull((await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id))
            .AdministratorApprovalRequestedAt);
    }

    [Fact]
    public async Task StartOverIsLimitedToTheCreatorAndAbandonsTheSession()
    {
        await using var db = await CreateDbAsync();
        var audit = new TestAuditEventWriter();
        var fleet = CreateFleet(db, audit);
        var (session, enrollment) = await SeedRedeemedAsync(db);
        // The service reloads the tracked session after cancelling, so keep the original hash here.
        var originalHandoffHash = session.HandoffSecretHash;
        fleet.SetupProgressReader = (id, _) => Progress(id, "restart-required", Now.AddMinutes(-10));

        var stranger = await fleet.CancelLocalSetupSessionAsync(session.Id, Guid.NewGuid());
        Assert.False(stranger.Succeeded);
        Assert.Equal("session_not_found", stranger.ErrorCode);
        Assert.Equal(LocalOfficeSetupSessionStatus.Redeemed,
            (await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);

        var cancelled = await fleet.CancelLocalSetupSessionAsync(session.Id, session.CreatedByUserId);

        Assert.True(cancelled.Succeeded, cancelled.Message);
        Assert.Equal("expired", cancelled.Session?.State);
        Assert.Equal("Setup cancelled", cancelled.Session?.PhaseDisplayName);
        var stored = await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(LocalOfficeSetupSessionStatus.Expired, stored.Status);
        Assert.Equal("setup_cancelled", stored.ErrorCode);
        Assert.NotEqual(originalHandoffHash, stored.HandoffSecretHash);
        Assert.Equal(ExecutionEnrollmentStatus.Revoked,
            (await db.ExecutionNodeEnrollments.AsNoTracking().SingleAsync(x => x.Id == enrollment.Id)).Status);
        Assert.Single(audit.Events, x => x.EventType == "office.local-setup.cancelled");

        // Idempotent: a second Start over reports the ended session without another audit record.
        var repeated = await fleet.CancelLocalSetupSessionAsync(session.Id, session.CreatedByUserId);
        Assert.True(repeated.Succeeded);
        Assert.Single(audit.Events, x => x.EventType == "office.local-setup.cancelled");
    }

    [Fact]
    public async Task StartOverIsRefusedWhileSetupIsStillReportingProgress()
    {
        await using var db = await CreateDbAsync();
        var fleet = CreateFleet(db, new TestAuditEventWriter());
        var (session, _) = await SeedRedeemedAsync(db);
        fleet.SetupProgressReader = (id, _) => Progress(id, "running", Now.AddMinutes(-1));

        var result = await fleet.CancelLocalSetupSessionAsync(session.Id, session.CreatedByUserId);

        Assert.False(result.Succeeded);
        Assert.Equal("setup_running", result.ErrorCode);
        Assert.Equal(LocalOfficeSetupSessionStatus.Redeemed,
            (await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task StartOverDoesNotAbandonRepairOrUpgrade()
    {
        await using var db = await CreateDbAsync();
        var fleet = CreateFleet(db, new TestAuditEventWriter());
        var (session, _) = await SeedRedeemedAsync(db, recoveryAction: "repair");

        var result = await fleet.CancelLocalSetupSessionAsync(session.Id, session.CreatedByUserId);

        Assert.False(result.Succeeded);
        Assert.Equal("cancel_not_available", result.ErrorCode);
    }

    [Fact]
    public async Task RedeemedSetupWithoutLiveOwnerOrProgressExpiresAsInterrupted()
    {
        await using var db = await CreateDbAsync();
        var audit = new TestAuditEventWriter();
        var fleet = CreateFleet(db, audit);
        var (session, enrollment) = await SeedRedeemedAsync(db, redeemedAt: Now.AddHours(-1));
        fleet.SetupProgressReader = (_, _) => null;

        await fleet.GetActiveLocalSetupSessionAsync(session.CreatedByUserId);

        var stored = await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(LocalOfficeSetupSessionStatus.Expired, stored.Status);
        Assert.Equal("office_setup_interrupted", stored.ErrorCode);
        Assert.Equal(ExecutionEnrollmentStatus.Revoked,
            (await db.ExecutionNodeEnrollments.AsNoTracking().SingleAsync(x => x.Id == enrollment.Id)).Status);
        Assert.Contains(audit.Events, x => x.EventType == "office.local-setup.interrupted-expired");
    }

    [Fact]
    public async Task InterruptedExpiryLeavesClaimedAndRestartPendingSessionsAlone()
    {
        await using var db = await CreateDbAsync();
        var fleet = CreateFleet(db, new TestAuditEventWriter());
        var (claimed, claimedEnrollment) = await SeedRedeemedAsync(db, redeemedAt: Now.AddHours(-1));
        claimedEnrollment.Status = ExecutionEnrollmentStatus.Claimed;
        var (pendingRestart, _) = await SeedRedeemedAsync(db, redeemedAt: Now.AddHours(-3));
        await db.SaveChangesAsync();
        fleet.SetupProgressReader = (id, _) => id == pendingRestart.Id
            ? Progress(id, "restart-required", Now.AddHours(-3)) : null;
        fleet.LastBootTimeReader = () => Now.AddDays(-2);

        await fleet.GetActiveLocalSetupSessionAsync(claimed.CreatedByUserId);
        await fleet.GetActiveLocalSetupSessionAsync(pendingRestart.CreatedByUserId);

        Assert.NotEqual(LocalOfficeSetupSessionStatus.Expired,
            (await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == claimed.Id)).Status);
        Assert.Equal(LocalOfficeSetupSessionStatus.Redeemed,
            (await db.LocalOfficeSetupSessions.AsNoTracking().SingleAsync(x => x.Id == pendingRestart.Id)).Status);
    }

    [Fact]
    public void NewLocalSetupRoutesRequireHostAdministration()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "CSweet.Api", "Setup", "SetupEndpoints.cs"));
        foreach (var route in new[] { "/local-sessions/{sessionId:guid}/continue\"", "/local-sessions/{sessionId:guid}/cancel\"" })
        {
            var start = source.IndexOf(route, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{route} is not mapped.");
            var end = source.IndexOf("group.Map", start + route.Length, StringComparison.Ordinal);
            var mapping = source[start..(end < 0 ? source.Length : end)];
            Assert.Contains(".RequireAuthorization(\"HostAdministration\")", mapping, StringComparison.Ordinal);
            Assert.Contains("GetApplicationUserId()", mapping, StringComparison.Ordinal);
        }
    }

    private static ExecutionFleetService.WindowsSetupProgressDocument Progress(Guid id, string state, DateTimeOffset observedAt) => new()
    {
        SchemaVersion = 1, JobId = id, State = state, PhaseKey = state == "restart-required" ? "windows-restart" : "certify-runtime",
        PhaseDisplayName = "Restart Windows to continue", Message = "Hyper-V needs a restart.", PercentComplete = 5,
        StartedAt = observedAt.AddMinutes(-5), UpdatedAt = observedAt, ObservedAt = observedAt, OwnerProcessId = 0
    };

    private static async Task<(LocalOfficeSetupSession Session, ExecutionNodeEnrollment Enrollment)> SeedRedeemedAsync(
        CSweetDbContext db, string? machineBinding = null, string recoveryAction = "none", DateTimeOffset? redeemedAt = null)
    {
        var pool = await db.ExecutionPools.FirstAsync();
        var enrollment = new ExecutionNodeEnrollment
        {
            Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, TokenHash = Sha256(Guid.NewGuid().ToString()),
            ReceiptHash = Sha256(Guid.NewGuid().ToString()), Status = ExecutionEnrollmentStatus.Available,
            ExpiresAt = Now.AddMinutes(10), CreatedAt = Now.AddMinutes(-20)
        };
        var redeemed = redeemedAt ?? Now.AddMinutes(-20);
        var session = new LocalOfficeSetupSession
        {
            Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), Status = LocalOfficeSetupSessionStatus.Redeemed,
            HandoffSecretHash = Sha256(Guid.NewGuid().ToString()), SetupReceiptHash = Sha256(Guid.NewGuid().ToString()),
            MachineBindingHash = machineBinding ?? MachineBinding("OFFICE-PC", "windows", "x64"),
            OperatingSystem = "windows", Architecture = "x64", ControlPlaneOrigin = "https://localhost:47082/",
            RecoveryAction = recoveryAction, PresetKey = "small", AllocatableCpuCount = 2, AllocatableMemoryMb = 4096,
            AllocatableDiskMb = 32768, MaximumConcurrentWorkloads = 1, ExecutionNodeEnrollmentId = enrollment.Id,
            CreatedAt = redeemed.AddMinutes(-1), RedeemedAt = redeemed, UpdatedAt = redeemed, ExpiresAt = redeemed.AddHours(2)
        };
        db.ExecutionNodeEnrollments.Add(enrollment);
        db.LocalOfficeSetupSessions.Add(session);
        await db.SaveChangesAsync();
        return (session, enrollment);
    }

    private static ExecutionFleetService CreateFleet(CSweetDbContext db, TestAuditEventWriter audit, ExecutionFleetOptions? options = null) =>
        new(db, audit, new FixedClock(Now), Options.Create(options ?? new ExecutionFleetOptions { PublicLaunchEnabled = true }),
            appConfiguration: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://localhost:47082/",
                ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
            }).Build());

    private static async Task<CSweetDbContext> CreateDbAsync()
    {
        var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await new SetupService(db).EnsureSeededAsync();
        await new ExecutionFleetService(db, new TestAuditEventWriter(), new FixedClock(Now)).EnsureDefaultPoolAsync();
        return db;
    }

    private static string MachineBinding(string machine, string os, string architecture) =>
        (string)typeof(ExecutionFleetService).GetMethod("MachineBindingHash", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [machine, os, architecture])!;

    private static string Sha256(string value) =>
        (string)typeof(ExecutionFleetService).GetMethod("Hash", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [value])!;

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
