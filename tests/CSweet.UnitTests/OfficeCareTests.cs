using CSweet.Api.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.UI.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;

namespace CSweet.UnitTests;

public sealed class OfficeCareTests
{
    [Theory]
    [InlineData("created", true)]
    [InlineData("redeemed", true)]
    [InlineData("connected", true)]
    [InlineData("removalinprogress", true)]
    [InlineData("ready", false)]
    [InlineData("failed", false)]
    [InlineData("recoveryrequired", false)]
    [InlineData("expired", false)]
    [InlineData(null, false)]
    public void PendingAndRunningOperationsKeepTheCareEntryInProgress(string? state, bool expected) =>
        Assert.Equal(expected, OfficeCareState.IsInProgress(state));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupChangeOnlySucceedsWhenWorkHasFinished(bool hasWork)
    {
        await using var db = CreateDb();
        var oldPool = new ExecutionPool { Id = Guid.NewGuid(), Name = "Original" };
        var newPool = new ExecutionPool { Id = Guid.NewGuid(), Name = "New" };
        var node = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPoolId = oldPool.Id, Name = "Old name", Status = ExecutionNodeStatus.Ready };
        db.ExecutionPools.AddRange(oldPool, newPool);
        db.ExecutionNodes.Add(node);
        if (hasWork) db.ExecutionWorkloadAssignments.Add(new() { Id = Guid.NewGuid(), ExecutionNodeId = node.Id, Status = ExecutionAssignmentStatus.Running });
        await db.SaveChangesAsync();
        var result = await ExecutionFleetEndpoints.UpdateOfficeSettingsAsync(node.Id, new(" Studio ", newPool.Id), db, TimeProvider.System, default);
        Assert.Equal(hasWork ? 400 : 200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(hasWork ? oldPool.Id : newPool.Id, node.ExecutionPoolId);
        Assert.Equal(hasWork ? "Old name" : "Studio", node.Name);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Name\n", true)]
    [InlineData("Valid", false)]
    public async Task InvalidNamesAndDisabledGroupsDoNotChangeSettings(string name, bool enabled)
    {
        await using var db = CreateDb();
        var pool = new ExecutionPool { Id = Guid.NewGuid(), IsEnabled = enabled };
        var node = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, Name = "Original", Status = ExecutionNodeStatus.Ready };
        db.ExecutionPools.Add(pool); db.ExecutionNodes.Add(node); await db.SaveChangesAsync();
        var result = await ExecutionFleetEndpoints.UpdateOfficeSettingsAsync(node.Id, new(name, pool.Id), db, TimeProvider.System, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal("Original", node.Name);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Theory]
    [InlineData(LocalOfficeSetupSessionStatus.Created, 400)]
    [InlineData(LocalOfficeSetupSessionStatus.Redeemed, 400)]
    [InlineData(LocalOfficeSetupSessionStatus.RemovalInProgress, 400)]
    [InlineData(LocalOfficeSetupSessionStatus.Connected, 200)]
    [InlineData(LocalOfficeSetupSessionStatus.Failed, 200)]
    public async Task SettingsStayLockedDuringInstallationButUnlockAfterItCompletes(LocalOfficeSetupSessionStatus status, int expected)
    {
        await using var db = CreateDb();
        var pool = new ExecutionPool { Id = Guid.NewGuid() };
        var node = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, Name = "Office", Status = ExecutionNodeStatus.Ready };
        db.ExecutionPools.Add(pool); db.ExecutionNodes.Add(node);
        db.LocalOfficeSetupSessions.Add(new() { Id = Guid.NewGuid(), UpgradeOfficeId = node.Id, Status = status, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync();
        var result = await ExecutionFleetEndpoints.UpdateOfficeSettingsAsync(node.Id, new("Updated name", pool.Id), db, TimeProvider.System, default);
        Assert.Equal(expected, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(expected == 200 ? "Updated name" : "Office", node.Name);
    }
}
