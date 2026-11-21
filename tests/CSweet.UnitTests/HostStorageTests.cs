using CSweet.Api.Compute;
using CSweet.Api.Setup;
using CSweet.Compute.Runtime;
using CSweet.Contracts.Setup;

namespace CSweet.UnitTests;

public sealed class HostStorageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("csweet-storage-").FullName;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void PreferenceSurvivesReloadAndRejectsUnknownOrIneligibleVolume()
    {
        var path = Path.Combine(root, "choice.json");
        var drive = new LocalOfficeVolumeResponse("registered", @"D:\", "", "NTFS", 100, 80, false, true, null, "", "");
        Assert.Null(HostStorageSettings.ReadPreference(path));
        Assert.True(HostStorageSettings.TrySave(path, new(drive.Id), [drive]));
        Assert.False(HostStorageSettings.TrySave(path, new(@"D:\supplied"), [drive]));
        Assert.False(HostStorageSettings.TrySave(path, new(drive.Id), [drive with { IsEligible = false }]));
        Assert.Equal(drive.Id, HostStorageSettings.ReadPreference(path));
        Assert.True(HostStorageSettings.TrySave(path, new(null), []));
        Assert.Null(HostStorageSettings.ReadPreference(path));
    }

    [Fact]
    public void VolumeWorkloadsAreScopedToTheExactProvider()
    {
        if (!OperatingSystem.IsWindows()) return;
        var business = Guid.NewGuid();
        var node = Guid.NewGuid();
        var path = ComputeVolumeStorage.Workloads('D', business, node);
        Assert.True(ComputeVolumeStorage.IsVolumeWorkload(path, business, node));
        Assert.False(ComputeVolumeStorage.IsVolumeWorkload(path, business, Guid.NewGuid()));
        Assert.False(ComputeVolumeStorage.IsVolumeWorkload(Path.Combine(path, ".."), business, node));
        Assert.False(ComputeVolumeStorage.IsVolumeWorkload(ComputeVolumeStorage.Root('D'), business, node));
    }

    [Fact]
    public void MeasuresFileLengthsAndDistinguishesMissingStorageFromEmptyStorage()
    {
        Assert.Equal((0L, "Measured"), HostStorageSettings.Measure(root, default));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllBytes(Path.Combine(root, "nested", "disk.vhdx"), new byte[57]);
        Assert.Equal((57L, "Measured"), HostStorageSettings.Measure(root, default));
        Assert.Equal((null, "Missing"), HostStorageSettings.Measure(Path.Combine(root, "absent"), default));
    }

    [Fact]
    public void DownloadObservationUsesOnlyTheMatchingInstallAttemptAndFixedDriveLayout()
    {
        var attempt = Guid.NewGuid();
        var receipt = Path.Combine(root, "storage.json");
        File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new { drive = "D", attemptId = attempt }));
        var legacy = ComputeDashboardEndpoints.DownloadCache(root, root, Guid.NewGuid()).FullName;
        Assert.EndsWith(Path.Combine("CSweet.Isolation", "artifacts", "linux-images", "cache"), legacy);
        Assert.EndsWith(Path.Combine(ComputeVolumeStorage.Root('D'), "build", "cache"),
            ComputeDashboardEndpoints.DownloadCache(root, root, attempt).FullName);
        File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new { drive = "../other", attemptId = attempt }));
        Assert.Equal(legacy, ComputeDashboardEndpoints.DownloadCache(root, root, attempt).FullName);
    }
}
