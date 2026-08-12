using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class ExecutionPlacementLoadTests
{
    [Fact]
    public void OneThousandNodesDeterministicallyPlaceTenThousandQueuedAssignments()
    {
        const int nodeCount = 1_000;
        const int assignmentCount = 10_000;
        var nodes = Enumerable.Range(0, nodeCount)
            .Select(index => new SimulatedNode(
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                16, 16 * 1024, 128 * 1024, 16))
            .ToArray();

        for (var assignment = 0; assignment < assignmentCount; assignment++)
        {
            SimulatedNode? selected = null;
            var selectedScore = double.PositiveInfinity;
            foreach (var node in nodes)
            {
                var score = ExecutionPlacementPolicy.Score(new ExecutionPlacementResources(
                    node.Cpu, node.MemoryMb, node.DiskMb, node.MaximumWorkloads,
                    node.ReservedCpu, node.ReservedMemoryMb, node.ReservedDiskMb,
                    node.ActiveWorkloads, 1, 512, 1024));
                if (!score.Fits || score.DominantUtilization > selectedScore) continue;
                if (score.DominantUtilization == selectedScore && selected is not null &&
                    node.Id.CompareTo(selected.Id) >= 0) continue;
                selected = node;
                selectedScore = score.DominantUtilization;
            }
            Assert.NotNull(selected);
            selected.ReservedCpu++;
            selected.ReservedMemoryMb += 512;
            selected.ReservedDiskMb += 1024;
            selected.ActiveWorkloads++;
        }

        Assert.Equal(assignmentCount, nodes.Sum(node => node.ActiveWorkloads));
        Assert.All(nodes, node => Assert.Equal(10, node.ActiveWorkloads));
        Assert.All(nodes, node => Assert.True(node.ReservedCpu <= node.Cpu));
        Assert.All(nodes, node => Assert.True(node.ReservedMemoryMb <= node.MemoryMb));
        Assert.All(nodes, node => Assert.True(node.ReservedDiskMb <= node.DiskMb));
    }

    private sealed class SimulatedNode(
        Guid id,
        int cpu,
        int memoryMb,
        int diskMb,
        int maximumWorkloads)
    {
        public Guid Id { get; } = id;
        public int Cpu { get; } = cpu;
        public int MemoryMb { get; } = memoryMb;
        public int DiskMb { get; } = diskMb;
        public int MaximumWorkloads { get; } = maximumWorkloads;
        public int ReservedCpu { get; set; }
        public int ReservedMemoryMb { get; set; }
        public int ReservedDiskMb { get; set; }
        public int ActiveWorkloads { get; set; }
    }
}
