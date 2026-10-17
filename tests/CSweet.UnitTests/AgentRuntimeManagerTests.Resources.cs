using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Theory]
    [InlineData(0, 4096)]
    [InlineData(8192, 8192)]
    public async Task Coding_allocation_uses_complete_guest_policy_and_pending_operation_request(int requested, int expected)
    {
        await using var db = CreateDb(); var installation = await SeedAsync(db);
        installation.PackageVersion!.ManifestJson = """{"runtime":{"environmentProfile":"software-development-polyglot-v1","workspaceAccess":"ReadWrite"}}""";
        db.AgentWorkItems.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, OrganizationId = installation.BusinessId,
            RequestedMemoryMb = requested, DeadlineAt = DateTimeOffset.UtcNow.AddHours(1) }); await db.SaveChangesAsync();
        var runner = new FakeRunner(); var manager = CreateManager(db, runner);
        await manager.ProcessDueSchedulesAsync(); await manager.ReconcileAsync();
        Assert.Equal(expected, Assert.Single(runner.Starts).Workload.ResourceLimits.MemoryMegabytes);
        Assert.Equal(expected, (await db.AgentRuntimeInstances.SingleAsync()).AllocatedMemoryMb);
    }
}
