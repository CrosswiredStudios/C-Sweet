using CSweet.Contracts.Setup;

namespace CSweet.Application.Setup;

public interface ILocalOfficeCapacityProbe
{
    LocalOfficeCapacityResponse GetCapacity();
}

public static class LocalOfficeCapacityCalculator
{
    public const int MinimumCpuCount = 1;
    public const int MinimumMemoryMb = 2048;
    public const int MinimumDiskMb = 32768;

    public static LocalOfficeCapacityResponse Calculate(
        int totalCpuCount,
        long totalMemoryBytes,
        long freeDiskBytes,
        bool isWindows) => Calculate(totalCpuCount, totalMemoryBytes, freeDiskBytes,
            isWindows ? "windows" : "unsupported");

    public static LocalOfficeCapacityResponse Calculate(
        int totalCpuCount,
        long totalMemoryBytes,
        long freeDiskBytes,
        string operatingSystem)
    {
        var platform = operatingSystem.Trim().ToLowerInvariant();
        var platformSupported = platform is "windows" or "linux";
        var totalMemoryMb = ToWholeMb(totalMemoryBytes);
        var freeDiskMb = ToWholeMb(freeDiskBytes);
        var reservedCpu = Math.Max(1, DivideRoundUp(Math.Max(1, totalCpuCount), 4));
        var reservedMemory = Math.Max(4096, totalMemoryMb / 4);
        var reservedDisk = Math.Max(20480, freeDiskMb / 10);
        var safeCpu = Math.Max(0, totalCpuCount - reservedCpu);
        var safeMemory = RoundDown(Math.Max(0, totalMemoryMb - reservedMemory), 1024);
        var safeDisk = RoundDown(Math.Max(0, freeDiskMb - reservedDisk), 1024);
        var supported = platformSupported && safeCpu >= MinimumCpuCount &&
            safeMemory >= MinimumMemoryMb && safeDisk >= MinimumDiskMb;
        var reason = !platformSupported
            ? "Assisted local setup is available on Windows and Linux."
            : supported ? null : "This machine does not have enough safe free capacity for C-Sweet Office.";

        return new LocalOfficeCapacityResponse(
            supported,
            reason,
            Math.Max(1, totalCpuCount),
            totalMemoryMb,
            freeDiskMb,
            reservedCpu,
            reservedMemory,
            reservedDisk,
            safeCpu,
            safeMemory,
            safeDisk,
            MinimumCpuCount,
            MinimumMemoryMb,
            MinimumDiskMb,
            supported
                ?
                [
                    Preset("small", "Small", 50, safeCpu, safeMemory, safeDisk),
                    Preset("balanced", "Balanced", 75, safeCpu, safeMemory, safeDisk),
                    Preset("performance", "Performance", 100, safeCpu, safeMemory, safeDisk)
                ]
                : []);
    }

    public static bool Contains(
        LocalOfficeCapacityResponse capacity,
        int cpuCount,
        int memoryMb,
        int diskMb) =>
        capacity.IsSupported &&
        cpuCount >= capacity.MinimumCpuCount && cpuCount <= capacity.SafeCpuCount &&
        memoryMb >= capacity.MinimumMemoryMb && memoryMb <= capacity.SafeMemoryMb &&
        diskMb >= capacity.MinimumDiskMb && diskMb <= capacity.SafeDiskMb &&
        memoryMb % 1024 == 0 && diskMb % 1024 == 0;

    public static int MaximumConcurrentWorkloads(int cpuCount) => Math.Max(1, cpuCount / 2);

    private static LocalOfficeResourcePresetResponse Preset(
        string key,
        string name,
        int percent,
        int safeCpu,
        int safeMemory,
        int safeDisk) => new(
            key,
            name,
            percent,
            Scale(safeCpu, percent, MinimumCpuCount, 1),
            Scale(safeMemory, percent, MinimumMemoryMb, 1024),
            Scale(safeDisk, percent, MinimumDiskMb, 1024));

    private static int Scale(int safeValue, int percent, int minimum, int quantum)
    {
        var value = RoundDown((int)((long)safeValue * percent / 100), quantum);
        return Math.Min(safeValue, Math.Max(minimum, value));
    }

    private static int ToWholeMb(long bytes) => (int)Math.Clamp(bytes / (1024L * 1024L), 0, int.MaxValue);
    private static int RoundDown(int value, int quantum) => value / quantum * quantum;
    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
}
