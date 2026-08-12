namespace CSweet.Infrastructure.Setup;

public sealed record ExecutionPlacementResources(
    int AllocatableCpu,
    int AllocatableMemoryMb,
    int AllocatableDiskMb,
    int MaximumWorkloads,
    int ReservedCpu,
    int ReservedMemoryMb,
    int ReservedDiskMb,
    int ActiveWorkloads,
    int RequestedCpu,
    int RequestedMemoryMb,
    int RequestedDiskMb);

public readonly record struct ExecutionPlacementScore(bool Fits, double DominantUtilization);

public static class ExecutionPlacementPolicy
{
    public static ExecutionPlacementScore Score(ExecutionPlacementResources resources)
    {
        if (resources.AllocatableCpu < 1 || resources.AllocatableMemoryMb < 1 ||
            resources.AllocatableDiskMb < 1 || resources.MaximumWorkloads < 1 ||
            resources.ReservedCpu < 0 || resources.ReservedMemoryMb < 0 ||
            resources.ReservedDiskMb < 0 || resources.ActiveWorkloads < 0 ||
            resources.RequestedCpu < 1 || resources.RequestedMemoryMb < 1 ||
            resources.RequestedDiskMb < 1)
            return new ExecutionPlacementScore(false, double.PositiveInfinity);

        var cpu = checked(resources.ReservedCpu + resources.RequestedCpu);
        var memory = checked(resources.ReservedMemoryMb + resources.RequestedMemoryMb);
        var disk = checked(resources.ReservedDiskMb + resources.RequestedDiskMb);
        var fits = resources.ActiveWorkloads < resources.MaximumWorkloads &&
            cpu <= resources.AllocatableCpu && memory <= resources.AllocatableMemoryMb &&
            disk <= resources.AllocatableDiskMb;
        var dominant = Math.Max(
            cpu / (double)resources.AllocatableCpu,
            Math.Max(memory / (double)resources.AllocatableMemoryMb,
                disk / (double)resources.AllocatableDiskMb));
        return new ExecutionPlacementScore(fits, dominant);
    }
}
