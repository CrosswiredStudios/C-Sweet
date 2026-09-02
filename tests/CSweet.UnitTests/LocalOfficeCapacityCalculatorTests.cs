using CSweet.Application.Setup;

namespace CSweet.UnitTests;

public sealed class LocalOfficeCapacityCalculatorTests
{
    [Fact]
    public void Calculate_ReservesHostCapacity_AndBuildsAdaptivePresets()
    {
        var capacity = LocalOfficeCapacityCalculator.Calculate(
            8, 16L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, true);

        Assert.True(capacity.IsSupported);
        Assert.Equal(2, capacity.ReservedCpuCount);
        Assert.Equal(4096, capacity.ReservedMemoryMb);
        Assert.Equal(20480, capacity.ReservedDiskMb);
        Assert.Equal((3, 6144, 40960), Values(capacity.Presets[0]));
        Assert.Equal((4, 9216, 61440), Values(capacity.Presets[1]));
        Assert.Equal((6, 12288, 81920), Values(capacity.Presets[2]));
    }

    [Fact]
    public void Calculate_RoundsDownAndClampsPresetsToMinimums()
    {
        var capacity = LocalOfficeCapacityCalculator.Calculate(
            2, 7L * 1024 * 1024 * 1024 + 777, 54L * 1024 * 1024 * 1024 + 777, true);

        Assert.True(capacity.IsSupported);
        Assert.All(capacity.Presets, preset =>
        {
            Assert.InRange(preset.CpuCount, capacity.MinimumCpuCount, capacity.SafeCpuCount);
            Assert.InRange(preset.MemoryMb, capacity.MinimumMemoryMb, capacity.SafeMemoryMb);
            Assert.InRange(preset.DiskMb, capacity.MinimumDiskMb, capacity.SafeDiskMb);
            Assert.Equal(0, preset.MemoryMb % 1024);
            Assert.Equal(0, preset.DiskMb % 1024);
        });
    }

    [Fact]
    public void Calculate_BlocksUnsupportedOrInsufficientHosts()
    {
        var nonWindows = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, false);
        var insufficient = LocalOfficeCapacityCalculator.Calculate(2, 4L << 30, 30L << 30, true);

        Assert.False(nonWindows.IsSupported);
        Assert.Empty(nonWindows.Presets);
        Assert.False(insufficient.IsSupported);
        Assert.Empty(insufficient.Presets);
    }

    [Fact]
    public void Calculate_SupportsLinuxHostsForManualLocalInstallation()
    {
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, "linux");

        Assert.True(capacity.IsSupported);
        Assert.Null(capacity.UnavailableReason);
        Assert.Equal(3, capacity.Presets.Count);
    }

    [Fact]
    public void Contains_EnforcesSafeBoundsAndWholeGigabyteStorageUnits()
    {
        var capacity = LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 100L << 30, true);

        Assert.True(LocalOfficeCapacityCalculator.Contains(capacity, 4, 8192, 40960));
        Assert.False(LocalOfficeCapacityCalculator.Contains(capacity, 4, 8193, 40960));
        Assert.False(LocalOfficeCapacityCalculator.Contains(capacity, capacity.SafeCpuCount + 1, 8192, 40960));
    }

    private static (int Cpu, int Memory, int Disk) Values(
        CSweet.Contracts.Setup.LocalOfficeResourcePresetResponse preset) =>
        (preset.CpuCount, preset.MemoryMb, preset.DiskMb);
}
