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
            Volumes = [new("data-volume", @"D:\", "Data", "NTFS", 1024L << 30, 500L << 30,
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

        var created = await fleet.CreateLocalSetupSessionAsync(new("balanced", preset.CpuCount, preset.MemoryMb,
            preset.DiskMb, StorageVolumeId: "data-volume"), userId);
        Assert.True(created.Succeeded);
        Assert.Equal("data-volume", created.Session!.StorageVolumeId);
        Assert.Equal(@"D:\CSweet\Office\data", created.Session.DataRoot);
        var restored = await fleet.GetLocalSetupSessionAsync(created.Session.Id, userId);
        Assert.Equal(created.Session.InstallRoot, restored.Session!.InstallRoot);
        var handoff = Uri.UnescapeDataString(new Uri(created.Session.LaunchUri!).Fragment["#handoff=".Length..]);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
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
    }
}
