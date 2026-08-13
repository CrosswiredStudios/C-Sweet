using CSweet.SatelliteOffice.Contracts.ControlPlane;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ExecutionFleetServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enrollment_IsOneUse_AndRequiresApprovalBeforeReadiness()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        await fleet.SelectOnboardingModeAsync(new("remote"));

        var enrollment = await fleet.CreateEnrollmentAsync();
        var token = Assert.IsType<string>(enrollment.Enrollment?.EnrollmentToken);
        var claim = await fleet.ClaimNodeAsync(Claim(token));
        var replay = await fleet.ClaimNodeAsync(Claim(token));

        Assert.True(claim.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.False(await fleet.IsReadyAsync());
        Assert.Equal(ExecutionNodeStatus.PendingApproval,
            (await db.ExecutionNodes.SingleAsync()).Status);

        var approval = await fleet.ApproveNodeAsync(claim.SatelliteOfficeId!.Value);
        var approvedNode = await db.ExecutionNodes.SingleAsync();
        approvedNode.LastHeartbeatAt = Now; // Simulates the first mTLS gateway heartbeat.
        await db.SaveChangesAsync();

        Assert.True(approval.Succeeded);
        Assert.True(await fleet.IsReadyAsync());
        Assert.True((await fleet.GetOnboardingStatusAsync()).IsReady);
    }

    [Fact]
    public async Task Enrollment_NormalizesIncomingCertificateAndCertificationTimesToUtc()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        await fleet.SelectOnboardingModeAsync(new("remote"));
        var enrollment = await fleet.CreateEnrollmentAsync();
        var token = Assert.IsType<string>(enrollment.Enrollment?.EnrollmentToken);
        var offset = TimeSpan.FromHours(-7);
        var claimRequest = Claim(token) with
        {
            CertificateExpiresAt = Now.AddYears(1).ToOffset(offset),
            Providers = [Claim(token).Providers[0] with
            {
                CertifiedAt = Now.AddHours(-1).ToOffset(offset),
                CertificationExpiresAt = Now.AddDays(1).ToOffset(offset)
            }]
        };

        Assert.True((await fleet.ClaimNodeAsync(claimRequest)).Succeeded);
        var node = await db.ExecutionNodes.Include(x => x.Providers).SingleAsync();
        Assert.Equal(TimeSpan.Zero, node.CertificateExpiresAt?.Offset);
        Assert.Equal(TimeSpan.Zero, Assert.Single(node.Providers).CertifiedAt.Offset);
        Assert.Equal(TimeSpan.Zero, Assert.Single(node.Providers).CertificationExpiresAt?.Offset);
    }

    [Fact]
    public async Task OfflineOrUncertifiedNode_DoesNotSatisfySetupCompletion()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var setupWithoutFleet = new SetupService(db);
        await setupWithoutFleet.EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var setup = new SetupService(db, fleet);

        var rejected = await setup.CompleteStepAsync("agent-execution");
        Assert.False(rejected.Succeeded);
        Assert.Equal("execution_node_required", rejected.ErrorCode);

        var enrollment = await fleet.CreateEnrollmentAsync();
        var claim = await fleet.ClaimNodeAsync(Claim(enrollment.Enrollment!.EnrollmentToken!, certified: false));
        var approval = await fleet.ApproveNodeAsync(claim.SatelliteOfficeId!.Value);
        Assert.False(approval.Succeeded);

        var provider = await db.ExecutionNodeProviders.SingleAsync();
        provider.IsAvailable = true;
        provider.GuestImageDigest = Digest('a');
        provider.CertificationEvidenceDigest = Digest('b');
        await db.SaveChangesAsync();
        Assert.True((await fleet.ApproveNodeAsync(claim.SatelliteOfficeId.Value)).Succeeded);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(await fleet.IsReadyAsync());
        Assert.False((await setup.CompleteStepAsync("agent-execution")).Succeeded);
    }

    [Fact]
    public async Task ExpiredEnrollmentCannotBeClaimed()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        clock.Advance(TimeSpan.FromMinutes(16));

        var claim = await fleet.ClaimNodeAsync(Claim(enrollment.Enrollment!.EnrollmentToken!));

        Assert.False(claim.Succeeded);
        Assert.Equal("invalid_enrollment", claim.ErrorCode);
    }

    [Fact]
    public async Task OnboardingReturnsNormalizedPublicGatewayAddressForRemoteInstaller()
    {
        await using var db = CreateDb();
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://execution.example.test/fleet/"
            }).Build();
        var fleet = CreateFleet(db, new MutableTimeProvider(Now), configuration);

        var status = await fleet.GetOnboardingStatusAsync();

        Assert.Equal("https://execution.example.test/fleet", status.Packages?.ControlPlaneUrl);
    }

    [Theory]
    [InlineData("host-a", "windows", "HOST-A", "windows", true)]
    [InlineData("host-a", "windows", "host-b", "windows", false)]
    [InlineData("host-a", "linux", "host-a", "windows", false)]
    public void LocalMachineIdentityRequiresMatchingMachineAndOperatingSystem(
        string machineName,
        string operatingSystem,
        string localMachineName,
        string localOperatingSystem,
        bool expected)
    {
        Assert.Equal(expected, ExecutionFleetService.IsLocalMachine(
            machineName, operatingSystem, localMachineName, localOperatingSystem));
    }

    [Fact]
    public async Task UnavailableProviderCanEnrollForDiagnosticsButCannotBeApproved()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var request = Claim(enrollment.Enrollment!.EnrollmentToken!) with
        {
            Providers = [new RegisterSatelliteOfficeProviderRequest(
                "firecracker-kvm", "1.0.0", "", "", "", "",
                DateTimeOffset.MinValue, null, true, true, false, "KVM is unavailable.")]
        };

        var claim = await fleet.ClaimNodeAsync(request);
        var approval = await fleet.ApproveNodeAsync(claim.SatelliteOfficeId!.Value);

        Assert.True(claim.Succeeded);
        Assert.False(approval.Succeeded);
        Assert.Equal("node_not_qualified", approval.ErrorCode);
        Assert.Equal("KVM is unavailable.", (await db.ExecutionNodeProviders.SingleAsync()).UnavailableReason);
    }

    [Fact]
    public async Task PendingNodeCanBeRejectedAndNeverBecomesReady()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var claim = await fleet.ClaimNodeAsync(Claim(enrollment.Enrollment!.EnrollmentToken!));

        var rejection = await fleet.RejectNodeAsync(claim.SatelliteOfficeId!.Value);

        Assert.True(rejection.Succeeded);
        Assert.Equal(ExecutionNodeStatus.Revoked, (await db.ExecutionNodes.SingleAsync()).Status);
        Assert.False(await fleet.IsReadyAsync());
        Assert.False((await fleet.ApproveNodeAsync(claim.SatelliteOfficeId.Value)).Succeeded);
    }

    [Fact]
    public async Task OperationalCertificateRotationRequiresCurrentNodeCertificate()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var claim = await fleet.ClaimNodeAsync(Claim(enrollment.Enrollment!.EnrollmentToken!));
        Assert.True((await fleet.ApproveNodeAsync(claim.SatelliteOfficeId!.Value)).Succeeded);
        var node = await db.ExecutionNodes.SingleAsync();

        var bootstrap = await fleet.GetOperationalCertificateAsync(
            node.Id, new SatelliteOfficeCertificateRequest(claim.EnrollmentReceipt!));
        var rejected = await fleet.RotateOperationalCertificateAsync(node.Id, "wrong", "wrong");
        var current = await fleet.RotateOperationalCertificateAsync(
            node.Id, node.CertificateThumbprint, node.CertificateSerialNumber);
        var legacyHeartbeat = await fleet.RecordHeartbeatAsync(node.Id,
            new SatelliteOfficeHeartbeatRequest(claim.EnrollmentReceipt!, node.SessionEpoch + 1,
                4, 4096, 32768, 2, Claim("unused").Providers));

        Assert.True(bootstrap.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Equal("node_certificate_rejected", rejected.ErrorCode);
        Assert.True(current.Succeeded);
        Assert.False(legacyHeartbeat);
    }

    [Fact]
    public async Task SeparateBuildAndRuntimeDefaultsRequireCapacityInBothPools()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);

        var firstEnrollment = await fleet.CreateEnrollmentAsync();
        var first = await fleet.ClaimNodeAsync(Claim(firstEnrollment.Enrollment!.EnrollmentToken!));
        Assert.True((await fleet.ApproveNodeAsync(first.SatelliteOfficeId!.Value)).Succeeded);
        var firstNode = await db.ExecutionNodes.SingleAsync(x => x.Id == first.SatelliteOfficeId);
        firstNode.LastHeartbeatAt = Now;

        var original = await db.ExecutionPools.SingleAsync();
        var runtime = new ExecutionPool
        {
            Id = Guid.NewGuid(), Name = "Runtime", IsEnabled = true, IsDefaultRuntimePool = true,
            MaximumActiveWorkloads = 100, CreatedAt = Now, UpdatedAt = Now
        };
        original.IsDefaultRuntimePool = false;
        db.ExecutionPools.Add(runtime);
        var settings = await db.AgentRuntimeGlobalSettings.SingleAsync();
        settings.DefaultRuntimeExecutionPoolId = runtime.Id;
        await db.SaveChangesAsync();

        Assert.False(await fleet.IsReadyAsync());
        var secondEnrollment = await fleet.CreateEnrollmentAsync();
        var second = await fleet.ClaimNodeAsync(Claim(secondEnrollment.Enrollment!.EnrollmentToken!));
        Assert.True((await fleet.ApproveNodeAsync(second.SatelliteOfficeId!.Value)).Succeeded);
        var secondNode = await db.ExecutionNodes.SingleAsync(x => x.Id == second.SatelliteOfficeId);
        secondNode.LastHeartbeatAt = Now;
        await db.SaveChangesAsync();

        Assert.True(await fleet.IsReadyAsync());
        Assert.Equal(2, await db.ExecutionPools.CountAsync());
    }

    [Fact]
    public async Task UnpinnedOrInsufficientNodeCannotCompleteOnboarding()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var wrongImage = Claim(enrollment.Enrollment!.EnrollmentToken!) with
        {
            AllocatableMemoryMb = 256,
            Providers = [Claim("unused").Providers[0] with { GuestImageDigest = Digest('c') }]
        };

        var claim = await fleet.ClaimNodeAsync(wrongImage);
        var rejected = await fleet.ApproveNodeAsync(claim.SatelliteOfficeId!.Value);

        Assert.False(rejected.Succeeded);
        Assert.Equal("node_not_qualified", rejected.ErrorCode);
        Assert.False(await fleet.IsReadyAsync());
    }

    [Fact]
    public async Task CertifiedNodeBelowBuilderCapacityRemainsNotReady()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var claim = await fleet.ClaimNodeAsync(Claim(enrollment.Enrollment!.EnrollmentToken!) with
        {
            AllocatableMemoryMb = 512,
            AllocatableDiskMb = 1024
        });

        Assert.True((await fleet.ApproveNodeAsync(claim.SatelliteOfficeId!.Value)).Succeeded);
        var node = await db.ExecutionNodes.SingleAsync();
        node.LastHeartbeatAt = Now;
        await db.SaveChangesAsync();

        Assert.False(await fleet.IsReadyAsync());
        Assert.False((await fleet.GetOnboardingStatusAsync()).IsReady);
    }

    private static ClaimSatelliteOfficeRequest Claim(string token, bool certified = true) => new(
        token, "node-1", "machine-1", "linux", "x64", "1.0.0", "1.0",
        "AABBCCDD", "0011", Now.AddYears(1),
        "-----BEGIN CERTIFICATE REQUEST-----\n" + new string('A', 128) + "\n-----END CERTIFICATE REQUEST-----",
        4, 4096, 32768, 2,
        [new RegisterSatelliteOfficeProviderRequest(
            "firecracker-kvm", "1.0.0", "1.0",
            Digest('a'), "production-v1",
            Digest('b'), Now.AddHours(-1), Now.AddDays(1),
            true, true, certified, certified ? null : "Certification missing.")]);

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
    private static ExecutionFleetService CreateFleet(
        CSweetDbContext db,
        TimeProvider clock,
        IConfiguration? configuration = null) =>
        new(db, new TestAuditEventWriter(), clock,
            Options.Create(new ExecutionFleetOptions { PublicLaunchEnabled = true }),
            new FakeCertificateAuthority(clock),
            configuration,
            runtimeOptions: Options.Create(new AgentRuntimeManagerOptions
            {
                RequiredCertificationSuiteVersion = "production-v1",
                BuilderGuestImageDigest = Digest('a'),
                RuntimeGuestImageDigest = Digest('a')
            }));
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class FakeCertificateAuthority(TimeProvider clock) : IExecutionNodeCertificateAuthority
    {
        public IssuedExecutionNodeCertificate Issue(string certificateSigningRequestPem, Guid nodeId) =>
            new(Convert.ToBase64String("test-certificate"u8),
                nodeId.ToString("N"), "1234", clock.GetUtcNow().AddDays(1));
    }

}
