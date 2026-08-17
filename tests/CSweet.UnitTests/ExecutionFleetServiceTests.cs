using CSweet.Office.Contracts.ControlPlane;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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

        var approval = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);
        var approvedNode = await db.ExecutionNodes.SingleAsync();
        approvedNode.LastHeartbeatAt = Now; // Simulates the first mTLS gateway heartbeat.
        await db.SaveChangesAsync();

        Assert.True(approval.Succeeded);
        Assert.True(await fleet.IsReadyAsync());
        Assert.True((await fleet.GetOnboardingStatusAsync()).IsReady);
    }

    [Fact]
    public async Task ApprovalRejectsNodeWithoutReliableSignedAssignmentSupport()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        await fleet.SelectOnboardingModeAsync(new("remote"));
        var enrollment = await fleet.CreateEnrollmentAsync();
        var token = Assert.IsType<string>(enrollment.Enrollment?.EnrollmentToken);
        var claim = await fleet.ClaimNodeAsync(Claim(token, officeVersion: "0.0.9"));

        var approval = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);

        Assert.False(approval.Succeeded);
        Assert.Contains("version 0.1.0 or later", approval.Message, StringComparison.Ordinal);
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
    public async Task Approval_NormalizesIssuedCertificateExpiryToUtc()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var localExpiry = Now.AddDays(1).ToOffset(TimeSpan.FromHours(-7));
        var fleet = CreateFleet(db, clock, certificateAuthority: new FixedCertificateAuthority(localExpiry));
        var enrollment = await fleet.CreateEnrollmentAsync();
        var claim = await fleet.ClaimNodeAsync(Claim(enrollment.Enrollment!.EnrollmentToken!));

        var approval = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);

        Assert.True(approval.Succeeded);
        Assert.Equal(TimeSpan.Zero, (await db.ExecutionNodes.SingleAsync()).CertificateExpiresAt?.Offset);
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
        var approval = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);
        Assert.False(approval.Succeeded);

        var provider = await db.ExecutionNodeProviders.SingleAsync();
        provider.IsAvailable = true;
        provider.GuestImageDigest = Digest('a');
        provider.CertificationEvidenceDigest = Digest('b');
        await db.SaveChangesAsync();
        Assert.True((await fleet.ApproveNodeAsync(claim.OfficeId.Value)).Succeeded);

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
    public async Task AssistedLocalSetup_IsMachineBoundSingleUse_AndAutoApprovesExactClaim()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
                ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
            }).Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets.Single(x => x.Key == "balanced");
        var userId = Guid.NewGuid();

        var created = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), userId);

        Assert.True(created.Succeeded);
        var launchUri = new Uri(Assert.IsType<string>(created.Session?.LaunchUri));
        Assert.Equal("created", created.Session?.State);
        var activeBeforeLaunch = await fleet.GetActiveLocalSetupSessionAsync(userId);
        Assert.Equal(created.Session?.Id, activeBeforeLaunch.Session?.Id);
        var duplicate = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), userId);
        Assert.True(duplicate.Succeeded);
        Assert.Equal("local_setup_in_progress", duplicate.ErrorCode);
        Assert.Equal(created.Session?.Id, duplicate.Session?.Id);
        Assert.Null(duplicate.Session?.LaunchUri);
        Assert.Single(await db.LocalOfficeSetupSessions.ToListAsync());
        var launchRequest = await fleet.LaunchLocalSetupSessionAsync(
            created.Session!.Id, userId, new(created.Session.LaunchUri!));
        Assert.True(launchRequest.Succeeded);
        Assert.Equal("created", launchRequest.Session?.State);
        Assert.NotNull(launchRequest.Session?.AdministratorApprovalRequestedAt);
        var handoff = Uri.UnescapeDataString(launchUri.Fragment["#handoff=".Length..]);
        var persisted = await db.LocalOfficeSetupSessions.SingleAsync();
        Assert.Equal(64, persisted.HandoffSecretHash.Length);
        Assert.DoesNotContain(handoff, persisted.HandoffSecretHash, StringComparison.Ordinal);

        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        var wrongMachine = await fleet.RedeemLocalSetupSessionAsync(new(
            handoff, "some-other-machine", "windows", architecture, "0.2.0"));
        Assert.False(wrongMachine.Succeeded);
        Assert.Equal("machine_mismatch", wrongMachine.ErrorCode);

        var redeemed = await fleet.RedeemLocalSetupSessionAsync(new(
            handoff, Environment.MachineName, "windows", architecture, "0.2.0"));
        var replay = await fleet.RedeemLocalSetupSessionAsync(new(
            handoff, Environment.MachineName, "windows", architecture, "0.2.0"));
        Assert.True(redeemed.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.Equal("invalid_handoff", replay.ErrorCode);

        clock.Advance(TimeSpan.FromMinutes(6)); // The redeemed installer may run beyond handoff expiry.
        var claim = Claim(Assert.IsType<string>(redeemed.EnrollmentToken), officeVersion: "0.2.0") with
        {
            MachineName = Environment.MachineName,
            OperatingSystem = "windows",
            Architecture = architecture,
            AllocatableCpuCount = redeemed.AllocatableCpuCount,
            AllocatableMemoryMb = redeemed.AllocatableMemoryMb,
            AllocatableDiskMb = redeemed.AllocatableDiskMb,
            MaximumConcurrentWorkloads = redeemed.MaximumConcurrentWorkloads
        };

        var claimed = await fleet.ClaimNodeAsync(claim);

        Assert.True(claimed.Succeeded);
        Assert.Equal(ExecutionNodeStatus.Ready, (await db.ExecutionNodes.SingleAsync()).Status);
        Assert.Equal(LocalOfficeSetupSessionStatus.Ready,
            (await db.LocalOfficeSetupSessions.SingleAsync()).Status);
        var readySession = (await fleet.GetLocalSetupSessionAsync(created.Session!.Id, userId)).Session;
        Assert.Equal("ready", readySession?.State);
        Assert.Equal("ready", readySession?.PhaseKey);
        Assert.Equal("Your Office is ready", readySession?.PhaseDisplayName);
    }

    [Fact]
    public async Task AssistedLocalSetup_CleanExistingOfficeRequiresBoundReconnectAndIssuesFreshSetupReceipt()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
                ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
            }).Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets.Single(x => x.Key == "balanced");
        var userId = Guid.NewGuid();
        var created = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), userId);
        var firstUri = new Uri(Assert.IsType<string>(created.Session?.LaunchUri));
        var firstHandoff = Uri.UnescapeDataString(firstUri.Fragment["#handoff=".Length..]);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();

        var detected = await fleet.PreflightLocalSetupSessionAsync(new(firstHandoff,
            Environment.MachineName, "windows", architecture, "0.3.0", "clean"));

        Assert.True(detected.Succeeded);
        Assert.False(detected.ProceedToRedemption);
        Assert.Equal("existing_office_detected", detected.ErrorCode);
        var recovery = await fleet.GetLocalSetupSessionAsync(created.Session!.Id, userId);
        Assert.Equal("recoveryrequired", recovery.Session?.State);
        Assert.True(recovery.Session?.RecoveryCanReconnect);

        var authorized = await fleet.SelectLocalSetupRecoveryAsync(created.Session.Id, userId, new("reconnect"));
        var reconnectUri = new Uri(Assert.IsType<string>(authorized.Session?.LaunchUri));
        var reconnectHandoff = Uri.UnescapeDataString(reconnectUri.Fragment["#handoff=".Length..]);
        Assert.NotEqual(firstHandoff, reconnectHandoff);
        var preflight = await fleet.PreflightLocalSetupSessionAsync(new(reconnectHandoff,
            Environment.MachineName, "windows", architecture, "0.3.0", "clean"));
        Assert.True(preflight.ProceedToRedemption);
        Assert.Equal("reconnect", preflight.ExistingInstallationAction);

        var redeemed = await fleet.RedeemLocalSetupSessionAsync(new(reconnectHandoff,
            Environment.MachineName, "windows", architecture, "0.3.0"));

        Assert.True(redeemed.Succeeded);
        Assert.Equal("reconnect", redeemed.ExistingInstallationAction);
        Assert.NotNull(redeemed.SetupReceipt);
        Assert.Equal(64, (await db.LocalOfficeSetupSessions.SingleAsync()).SetupReceiptHash?.Length);
        Assert.False(await fleet.ReportLocalSetupResultAsync(new(AssistedSetupSessionId: created.Session.Id,
            SetupReceipt: redeemed.SetupReceipt!, ResultCode: "reconnect_unsafe",
            MachineName: "another-machine", OperatingSystem: "windows", Architecture: architecture)));
        Assert.True(await fleet.ReportLocalSetupResultAsync(new(created.Session.Id,
            redeemed.SetupReceipt!, "reconnect_unsafe", Environment.MachineName, "windows", architecture)));
        Assert.False(await fleet.ReportLocalSetupResultAsync(new(created.Session.Id,
            redeemed.SetupReceipt!, "reconnect_unsafe", Environment.MachineName, "windows", architecture)));
    }

    [Fact]
    public async Task AssistedLocalSetup_ActiveExistingOfficeBlocksReconnectButAllowsConfirmedRemoval()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
                ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
            }).Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets.Single(x => x.Key == "balanced");
        var userId = Guid.NewGuid();
        var created = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), userId);
        var firstUri = new Uri(Assert.IsType<string>(created.Session?.LaunchUri));
        var firstHandoff = Uri.UnescapeDataString(firstUri.Fragment["#handoff=".Length..]);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        await fleet.PreflightLocalSetupSessionAsync(new(firstHandoff,
            Environment.MachineName, "windows", architecture, "0.3.0", "active"));

        var reconnect = await fleet.SelectLocalSetupRecoveryAsync(created.Session!.Id, userId, new("reconnect"));
        Assert.False(reconnect.Succeeded);
        Assert.Equal("reconnect_not_safe", reconnect.ErrorCode);

        var remove = await fleet.SelectLocalSetupRecoveryAsync(created.Session.Id, userId, new("remove"));
        var removeUri = new Uri(Assert.IsType<string>(remove.Session?.LaunchUri));
        var removeHandoff = Uri.UnescapeDataString(removeUri.Fragment["#handoff=".Length..]);
        var removalPreflight = await fleet.PreflightLocalSetupSessionAsync(new(removeHandoff,
            Environment.MachineName, "windows", architecture, "0.3.0", "active"));
        Assert.Equal("remove", removalPreflight.ExistingInstallationAction);
        Assert.False(removalPreflight.ProceedToRedemption);
        Assert.NotNull(removalPreflight.SetupReceipt);

        Assert.True(await fleet.CompleteLocalOfficeRemovalAsync(new(removeHandoff,
            Environment.MachineName, "windows", architecture)));
        Assert.True(await fleet.CompleteLocalOfficeRemovalAsync(new(removeHandoff,
            Environment.MachineName, "windows", architecture)));
        Assert.Equal(LocalOfficeSetupSessionStatus.Removed,
            (await db.LocalOfficeSetupSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task AssistedLocalSetup_ReconcilesExactEnrollmentClaimStrandedByLauncher()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
                ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
            }).Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets.Single(x => x.Key == "balanced");
        var userId = Guid.NewGuid();
        var created = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), userId);
        var uri = new Uri(Assert.IsType<string>(created.Session?.LaunchUri));
        var handoff = Uri.UnescapeDataString(uri.Fragment["#handoff=".Length..]);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        var redeemed = await fleet.RedeemLocalSetupSessionAsync(new(
            handoff, Environment.MachineName, "windows", architecture, "0.2.0"));
        var session = await db.LocalOfficeSetupSessions.SingleAsync();
        var enrollmentId = Assert.IsType<Guid>(session.ExecutionNodeEnrollmentId);

        // Simulate the previous development launcher omitting the assisted-session binding.
        session.ExecutionNodeEnrollmentId = null;
        await db.SaveChangesAsync();
        var claim = Claim(Assert.IsType<string>(redeemed.EnrollmentToken), officeVersion: "0.2.0") with
        {
            MachineName = Environment.MachineName,
            OperatingSystem = "windows",
            Architecture = architecture,
            AllocatableCpuCount = redeemed.AllocatableCpuCount,
            AllocatableMemoryMb = redeemed.AllocatableMemoryMb,
            AllocatableDiskMb = redeemed.AllocatableDiskMb,
            MaximumConcurrentWorkloads = redeemed.MaximumConcurrentWorkloads
        };
        Assert.True((await fleet.ClaimNodeAsync(claim)).Succeeded);
        Assert.Equal(ExecutionNodeStatus.PendingApproval, (await db.ExecutionNodes.SingleAsync()).Status);
        session.ExecutionNodeEnrollmentId = enrollmentId;
        await db.SaveChangesAsync();

        var recovered = await fleet.GetLocalSetupSessionAsync(created.Session!.Id, userId);

        Assert.Equal("ready", recovered.Session?.State);
        Assert.Equal(ExecutionNodeStatus.Ready, (await db.ExecutionNodes.SingleAsync()).Status);
        Assert.Equal(LocalOfficeSetupSessionStatus.Ready, session.Status);
    }

    [Fact]
    public async Task AssistedLocalSetup_RejectsOfficeThatDropsSelectedAllocation()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
                ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
            }).Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(16, 32L << 30, 200L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets.Single(x => x.Key == "small");
        var created = await fleet.CreateLocalSetupSessionAsync(
            new("small", selected.CpuCount, selected.MemoryMb, selected.DiskMb), Guid.NewGuid());
        var uri = new Uri(Assert.IsType<string>(created.Session?.LaunchUri));
        var handoff = Uri.UnescapeDataString(uri.Fragment["#handoff=".Length..]);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        var redeemed = await fleet.RedeemLocalSetupSessionAsync(new(
            handoff, Environment.MachineName, "windows", architecture, "0.2.0"));
        var claim = Claim(Assert.IsType<string>(redeemed.EnrollmentToken), officeVersion: "0.2.0") with
        {
            MachineName = Environment.MachineName,
            OperatingSystem = "windows",
            Architecture = architecture,
            AllocatableCpuCount = 4,
            AllocatableMemoryMb = 4096,
            AllocatableDiskMb = 32768,
            MaximumConcurrentWorkloads = 2
        };

        var result = await fleet.ClaimNodeAsync(claim);

        Assert.False(result.Succeeded);
        Assert.Equal("assisted_allocation_mismatch", result.ErrorCode);
        Assert.Empty(await db.ExecutionNodes.ToListAsync());
        Assert.Equal(LocalOfficeSetupSessionStatus.Redeemed,
            (await db.LocalOfficeSetupSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task AssistedLocalSetup_UnredeemedHandoffExpiresAfterFiveMinutes()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test" })
            .Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets[0];
        var created = await fleet.CreateLocalSetupSessionAsync(
            new("small", selected.CpuCount, selected.MemoryMb, selected.DiskMb), Guid.NewGuid());
        var uri = new Uri(created.Session!.LaunchUri!);
        var handoff = Uri.UnescapeDataString(uri.Fragment["#handoff=".Length..]);
        clock.Advance(TimeSpan.FromMinutes(6));

        var redemption = await fleet.RedeemLocalSetupSessionAsync(new(handoff, Environment.MachineName,
            "windows", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "0.2.0"));

        Assert.False(redemption.Succeeded);
        Assert.Equal("invalid_handoff", redemption.ErrorCode);
    }

    [Fact]
    public async Task AssistedLocalSetup_RefreshesHandoffWithoutCreatingAnotherSession()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test" })
            .Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity));
        var selected = capacity.Presets.Single(x => x.Key == "balanced");
        var userId = Guid.NewGuid();
        var created = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), userId);
        var firstUri = new Uri(created.Session!.LaunchUri!);
        var firstHandoff = Uri.UnescapeDataString(firstUri.Fragment["#handoff=".Length..]);
        Assert.True((await fleet.LaunchLocalSetupSessionAsync(
            created.Session.Id, userId, new(created.Session.LaunchUri!))).Succeeded);

        var refreshed = await fleet.RefreshLocalSetupSessionHandoffAsync(created.Session.Id, userId);

        Assert.True(refreshed.Succeeded);
        Assert.Equal(created.Session.Id, refreshed.Session?.Id);
        Assert.Null(refreshed.Session?.AdministratorApprovalRequestedAt);
        Assert.Single(await db.LocalOfficeSetupSessions.ToListAsync());
        var secondUri = new Uri(Assert.IsType<string>(refreshed.Session?.LaunchUri));
        var secondHandoff = Uri.UnescapeDataString(secondUri.Fragment["#handoff=".Length..]);
        Assert.NotEqual(firstHandoff, secondHandoff);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        Assert.False((await fleet.RedeemLocalSetupSessionAsync(new(firstHandoff, Environment.MachineName,
            "windows", architecture, "0.2.0"))).Succeeded);
        Assert.True((await fleet.RedeemLocalSetupSessionAsync(new(secondHandoff, Environment.MachineName,
            "windows", architecture, "0.2.0"))).Succeeded);
    }

    [Fact]
    public async Task AssistedLocalSetup_ProbesExactLoopbackCertificateFingerprint()
    {
        var persistedKeyName = OperatingSystem.IsWindows() ? $"csweet-test-{Guid.NewGuid():N}" : null;
        try
        {
            RSA certificateKey;
            if (OperatingSystem.IsWindows())
            {
                certificateKey = new RSACng(CngKey.Create(CngAlgorithm.Rsa, persistedKeyName,
                    new CngKeyCreationParameters
                    {
                        Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                        ExportPolicy = CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport,
                        KeyUsage = CngKeyUsages.AllUsages
                    }));
            }
            else
            {
                certificateKey = RSA.Create(2048);
            }
            using var key = certificateKey;
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddDnsName("localhost");
            request.CertificateExtensions.Add(subjectAlternativeName.Build());
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                using var tls = new SslStream(connection.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
                await Task.Delay(250);
            });
            try
            {
                string actual;
                try
                {
                    actual = await ExecutionFleetService.ProbeServerCertificateSha256Async(
                        new Uri($"https://localhost:{port}"));
                }
                catch
                {
                    await server;
                    throw;
                }

                Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant(), actual);
                await server;
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows() && persistedKeyName is not null)
            {
                try
                {
                    using var persistedKey = CngKey.Open(
                        persistedKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                    persistedKey.Delete();
                }
                catch (CryptographicException)
                {
                    // RSACng can remove the temporary persisted key when its final handle closes.
                }
            }
        }
    }

    [Fact]
    public async Task AssistedLocalSetup_CanStartBeforeWindowsPackageIsPublished()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test" })
            .Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 32L << 30, 100L << 30, true);
        var fleet = CreateFleet(db, clock, configuration,
            capacityProbe: new FixedCapacityProbe(capacity), windowsPackageUrl: null);
        var selected = capacity.Presets.Single(x => x.Key == "balanced");

        var created = await fleet.CreateLocalSetupSessionAsync(
            new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), Guid.NewGuid());

        Assert.True(created.Succeeded);
        Assert.Null(created.Session?.WindowsPackageUrl);
        Assert.StartsWith("csweet-office://enroll/v1", created.Session?.LaunchUri, StringComparison.Ordinal);
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
            Providers = [new RegisterOfficeProviderRequest(
                "firecracker-kvm", "1.0.0", "", "", "", "",
                DateTimeOffset.MinValue, null, true, true, false, "KVM is unavailable.")]
        };

        var claim = await fleet.ClaimNodeAsync(request);
        var approval = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);

        Assert.True(claim.Succeeded);
        Assert.False(approval.Succeeded);
        Assert.Equal("node_not_qualified", approval.ErrorCode);
        Assert.Contains("Provider firecracker-kvm is unavailable: KVM is unavailable.", approval.Message, StringComparison.Ordinal);
        Assert.Equal("KVM is unavailable.", (await db.ExecutionNodeProviders.SingleAsync()).UnavailableReason);
    }

    [Fact]
    public async Task ApprovalDoesNotExposeSerializedPowerShellDiagnostics()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var request = Claim(enrollment.Enrollment!.EnrollmentToken!) with
        {
            Providers = [new RegisterOfficeProviderRequest(
                "hyperv-gen2", "1.0.0", "", "", "", "",
                DateTimeOffset.MinValue, null, true, true, false,
                "#< CLIXML<Objs Version=\"1.1.0.1\"><Obj S=\"progress\" /></Objs>")]
        };

        var claim = await fleet.ClaimNodeAsync(request);
        var approval = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);

        Assert.False(approval.Succeeded);
        Assert.DoesNotContain("CLIXML", approval.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RuntimeHost event log", approval.Message, StringComparison.Ordinal);
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

        var rejection = await fleet.RejectNodeAsync(claim.OfficeId!.Value);

        Assert.True(rejection.Succeeded);
        Assert.Equal(ExecutionNodeStatus.Revoked, (await db.ExecutionNodes.SingleAsync()).Status);
        Assert.False(await fleet.IsReadyAsync());
        Assert.False((await fleet.ApproveNodeAsync(claim.OfficeId.Value)).Succeeded);
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
        Assert.True((await fleet.ApproveNodeAsync(claim.OfficeId!.Value)).Succeeded);
        var node = await db.ExecutionNodes.SingleAsync();

        var bootstrap = await fleet.GetOperationalCertificateAsync(
            node.Id, new OfficeCertificateRequest(claim.EnrollmentReceipt!));
        var rejected = await fleet.RotateOperationalCertificateAsync(node.Id, "wrong", "wrong");
        var current = await fleet.RotateOperationalCertificateAsync(
            node.Id, node.CertificateThumbprint, node.CertificateSerialNumber);
        var legacyHeartbeat = await fleet.RecordHeartbeatAsync(node.Id,
            new OfficeHeartbeatRequest(claim.EnrollmentReceipt!, node.SessionEpoch + 1,
                4, 4096, 32768, 2, Claim("unused").Providers));

        Assert.True(bootstrap.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Equal("node_certificate_rejected", rejected.ErrorCode);
        Assert.True(current.Succeeded);
        Assert.False(legacyHeartbeat);
    }

    [Fact]
    public async Task BootstrapHeartbeatUpdatesProviderInventoryWithoutReplacingExistingRows()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var fleet = CreateFleet(db, clock);
        var enrollment = await fleet.CreateEnrollmentAsync();
        var claimRequest = Claim(enrollment.Enrollment!.EnrollmentToken!);
        var claim = await fleet.ClaimNodeAsync(claimRequest);
        var originalProviderId = (await db.ExecutionNodeProviders.SingleAsync()).Id;
        var updatedProvider = claimRequest.Providers[0] with
        {
            ProviderVersion = "1.0.1",
            CertifiedAt = Now.AddMinutes(-5),
            CertificationExpiresAt = Now.AddDays(2)
        };

        var accepted = await fleet.RecordHeartbeatAsync(
            claim.OfficeId!.Value,
            new OfficeHeartbeatRequest(claim.EnrollmentReceipt!, 1, 8, 8192, 65536, 4, [updatedProvider]));

        var provider = await db.ExecutionNodeProviders.SingleAsync();
        Assert.True(accepted);
        Assert.Equal(originalProviderId, provider.Id);
        Assert.Equal("1.0.1", provider.ProviderVersion);
        Assert.Equal(Now.AddMinutes(-5), provider.CertifiedAt);
        var node = await db.ExecutionNodes.SingleAsync();
        Assert.Equal(1, node.SessionEpoch);
        Assert.Equal(8, node.AllocatableCpuCount);
        Assert.Equal(Now, node.LastHeartbeatAt);
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
        Assert.True((await fleet.ApproveNodeAsync(first.OfficeId!.Value)).Succeeded);
        var firstNode = await db.ExecutionNodes.SingleAsync(x => x.Id == first.OfficeId);
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
        Assert.True((await fleet.ApproveNodeAsync(second.OfficeId!.Value)).Succeeded);
        var secondNode = await db.ExecutionNodes.SingleAsync(x => x.Id == second.OfficeId);
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
        var rejected = await fleet.ApproveNodeAsync(claim.OfficeId!.Value);

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

        Assert.True((await fleet.ApproveNodeAsync(claim.OfficeId!.Value)).Succeeded);
        var node = await db.ExecutionNodes.SingleAsync();
        node.LastHeartbeatAt = Now;
        await db.SaveChangesAsync();

        Assert.False(await fleet.IsReadyAsync());
        Assert.False((await fleet.GetOnboardingStatusAsync()).IsReady);
    }

    private static ClaimOfficeRequest Claim(
        string token,
        bool certified = true,
        string officeVersion = "1.0.2") => new(
        token, "node-1", "machine-1", "linux", "x64", officeVersion, "1.0",
        "AABBCCDD", "0011", Now.AddYears(1),
        "-----BEGIN CERTIFICATE REQUEST-----\n" + new string('A', 128) + "\n-----END CERTIFICATE REQUEST-----",
        4, 4096, 32768, 2,
        [new RegisterOfficeProviderRequest(
            "firecracker-kvm", "1.0.0", "1.0",
            Digest('a'), "production-v1",
            Digest('b'), Now.AddHours(-1), Now.AddDays(1),
            true, true, certified, certified ? null : "Certification missing.")]);

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
    private static ExecutionFleetService CreateFleet(
        CSweetDbContext db,
        TimeProvider clock,
        IConfiguration? configuration = null,
        IExecutionNodeCertificateAuthority? certificateAuthority = null,
        ILocalOfficeCapacityProbe? capacityProbe = null,
        string? windowsPackageUrl = "https://downloads.example.test/csweet-office.msi") =>
        new(db, new TestAuditEventWriter(), clock,
            Options.Create(new ExecutionFleetOptions
            {
                PublicLaunchEnabled = true,
                WindowsPackageOverrideUrl = windowsPackageUrl
            }),
            certificateAuthority ?? new FakeCertificateAuthority(clock),
            configuration,
            runtimeOptions: Options.Create(new AgentRuntimeManagerOptions
            {
                RequiredCertificationSuiteVersion = "production-v1",
                BuilderGuestImageDigest = Digest('a'),
                RuntimeGuestImageDigest = Digest('a')
            }),
            localCapacityProbe: capacityProbe);
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

    private sealed class FixedCertificateAuthority(DateTimeOffset expiresAt) : IExecutionNodeCertificateAuthority
    {
        public IssuedExecutionNodeCertificate Issue(string certificateSigningRequestPem, Guid nodeId) =>
            new(Convert.ToBase64String("test-certificate"u8), nodeId.ToString("N"), "1234", expiresAt);
    }

    private sealed class FixedCapacityProbe(LocalOfficeCapacityResponse capacity) : ILocalOfficeCapacityProbe
    {
        public LocalOfficeCapacityResponse GetCapacity() => capacity;
    }

}
