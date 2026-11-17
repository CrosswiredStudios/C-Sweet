using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.UnitTests;

public sealed partial class ExecutionFleetServiceTests
{
    [Fact]
    public async Task SelectedStoragePersistsAndReachesAuthenticatedInstallerHandoff()
    {
        await using var db = CreateDb();
        await new SetupService(db).EnsureSeededAsync();
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 10L << 30, true) with
        {
            Volumes = [new("system-volume", @"C:\", "System", "NTFS", 1024L << 30, 500L << 30,
                true, true, null, @"C:\Program Files\CSweet\Office", @"C:\ProgramData\CSweet\Office"),
                new("data-volume", @"D:\", "Data", "NTFS", 1024L << 30, 500L << 30,
                false, true, null, @"D:\CSweet\Office\program", @"D:\CSweet\Office\data")]
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
            ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
        }).Build();
        var fleet = CreateFleet(db, new MutableTimeProvider(Now), config, capacityProbe: new FixedCapacityProbe(capacity));
        var limits = LocalOfficeCapacityCalculator.ForVolume(capacity, "data-volume");
        var preset = limits.Presets.Single(x => x.Key == "balanced");
        var userId = Guid.NewGuid();
        var invalid = await fleet.CreateLocalSetupSessionAsync(new("balanced", preset.CpuCount, preset.MemoryMb,
            preset.DiskMb, StorageVolumeId: "missing"), userId);
        Assert.False(invalid.Succeeded);
        Assert.Empty(await db.LocalOfficeSetupSessions.ToListAsync());

        var first = await fleet.CreateLocalSetupSessionAsync(new("balanced", preset.CpuCount, preset.MemoryMb,
            preset.DiskMb, StorageVolumeId: "system-volume"), userId);
        Assert.True(first.Succeeded);
        Assert.Equal(@"C:\ProgramData\CSweet\Office", first.Session!.DataRoot);
        var firstHandoff = Uri.UnescapeDataString(new Uri(first.Session!.LaunchUri!).Fragment["#handoff=".Length..]);
        var denied = await fleet.CancelLocalSetupSessionAsync(first.Session.Id, Guid.NewGuid());
        Assert.False(denied.Succeeded);
        Assert.Equal("session_not_found", denied.ErrorCode);
        Assert.True((await fleet.CancelLocalSetupSessionAsync(first.Session.Id, userId)).Succeeded);
        Assert.True((await fleet.CancelLocalSetupSessionAsync(first.Session.Id, userId)).Succeeded);
        Assert.Null((await fleet.GetActiveLocalSetupSessionAsync(userId)).Session);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Assert.False((await fleet.RedeemLocalSetupSessionAsync(new(firstHandoff, Environment.MachineName, "windows", architecture, "0.13.0"))).Succeeded);
        Assert.False((await fleet.LaunchLocalSetupSessionAsync(first.Session.Id, userId, new(first.Session.LaunchUri!))).Succeeded);

        var created = await fleet.CreateLocalSetupSessionAsync(new("balanced", preset.CpuCount, preset.MemoryMb,
            preset.DiskMb, StorageVolumeId: "data-volume"), userId);
        Assert.True(created.Succeeded);
        Assert.NotEqual(first.Session.Id, created.Session!.Id);
        Assert.Equal("data-volume", created.Session!.StorageVolumeId);
        Assert.Equal(@"D:\CSweet\Office\data", created.Session.DataRoot);
        var restored = await fleet.GetLocalSetupSessionAsync(created.Session.Id, userId);
        Assert.Equal(created.Session.InstallRoot, restored.Session!.InstallRoot);
        var handoff = Uri.UnescapeDataString(new Uri(created.Session.LaunchUri!).Fragment["#handoff=".Length..]);
        var old = await fleet.PreflightLocalSetupSessionAsync(new(handoff, Environment.MachineName, "windows", architecture, "0.12.0", "none"));
        Assert.False(old.Succeeded);
        Assert.Equal("storage_installer_update_required", old.ErrorCode);
        var oldRedemption = await fleet.RedeemLocalSetupSessionAsync(new(handoff, Environment.MachineName, "windows", architecture, "0.12.0"));
        Assert.False(oldRedemption.Succeeded);
        var preflight = await fleet.PreflightLocalSetupSessionAsync(new(handoff, Environment.MachineName, "windows", architecture, "0.13.0", "none"));
        Assert.True(preflight.ProceedToRedemption);
        Assert.Equal(created.Session.InstallRoot, preflight.Storage!.InstallRoot);
        var redemption = await fleet.RedeemLocalSetupSessionAsync(new(handoff, Environment.MachineName, "windows", architecture, "0.13.0"));
        Assert.True(redemption.Succeeded);
        Assert.Equal(preflight.Storage, redemption.Storage);
        var cannotCancel = await fleet.CancelLocalSetupSessionAsync(created.Session.Id, userId);
        Assert.False(cannotCancel.Succeeded);
        Assert.Equal("setup_cannot_cancel", cannotCancel.ErrorCode);
    }

    [Fact]
    public async Task DriveChangeCannotCancelAnAlreadyRequestedWindowsApproval()
    {
        await using var db = CreateDb();
        await new SetupService(db).EnsureSeededAsync();
        var userId = Guid.NewGuid();
        var session = new CSweet.Domain.Setup.LocalOfficeSetupSession
        {
            Id = Guid.NewGuid(), CreatedByUserId = userId, HandoffSecretHash = new string('a', 64),
            MachineBindingHash = new string('b', 64), OperatingSystem = "windows", Architecture = "x64",
            ControlPlaneOrigin = "https://office.example.test", PresetKey = "balanced", RecoveryAction = "none",
            AdministratorApprovalRequestedAt = Now, ExpiresAt = Now.AddMinutes(5), CreatedAt = Now, UpdatedAt = Now
        };
        db.LocalOfficeSetupSessions.Add(session);
        await db.SaveChangesAsync();
        var result = await CreateFleet(db, new MutableTimeProvider(Now)).CancelLocalSetupSessionAsync(session.Id, userId);
        Assert.False(result.Succeeded);
        Assert.Equal("setup_cannot_cancel", result.ErrorCode);
        Assert.Equal(CSweet.Domain.Setup.LocalOfficeSetupSessionStatus.Created, session.Status);
        Assert.Equal(new string('a', 64), session.HandoffSecretHash);
    }
}
