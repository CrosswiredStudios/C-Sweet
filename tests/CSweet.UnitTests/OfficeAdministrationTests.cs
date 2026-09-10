using CSweet.Api.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class OfficeAdministrationTests
{
    [Fact]
    public async Task DetailActivityCountsAllActiveWorkAndFiltersHistoryToTheOffice()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var office = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.ExecutionNodes.AddRange(new ExecutionNode { Id = office }, new ExecutionNode { Id = other });
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 250; i++)
            db.ExecutionWorkloadAssignments.Add(new ExecutionWorkloadAssignment { Id = Guid.NewGuid(),
                ExecutionNodeId = other, QueuedAt = now, Status = ExecutionAssignmentStatus.Running });
        for (var i = 0; i < 60; i++)
            db.ExecutionWorkloadAssignments.Add(new ExecutionWorkloadAssignment { Id = Guid.NewGuid(),
                ExecutionNodeId = office, QueuedAt = now.AddMinutes(-i), Status = ExecutionAssignmentStatus.Completed });
        db.ExecutionWorkloadAssignments.Add(new ExecutionWorkloadAssignment { Id = Guid.NewGuid(),
            ExecutionNodeId = office, QueuedAt = now.AddDays(-1), Status = ExecutionAssignmentStatus.Running });
        await db.SaveChangesAsync();
        var activity = await ExecutionFleetEndpoints.GetOfficeActivityAsync(db, office);
        Assert.NotNull(activity);
        Assert.Equal(1, activity.ActiveAssignmentCount);
        Assert.Equal(50, activity.RecentAssignments.Count);
        Assert.All(activity.RecentAssignments, x => Assert.Equal(office, x.ExecutionNodeId));
        Assert.Null(await ExecutionFleetEndpoints.GetOfficeActivityAsync(db, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("draining", 0, 0, true)]
    [InlineData("draining", 0, 31, false)]
    [InlineData("draining", 1, 0, false)]
    [InlineData("draining", null, 0, false)]
    [InlineData("ready", 0, 0, false)]
    [InlineData("offline", 0, 0, false)]
    [InlineData("revoked", 0, 0, false)]
    public void UpgradeReadinessRequiresFreshConnectedDrainedOfficeAndKnownZeroWork(string status, int? active, int ageSeconds, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var office = new ExecutionNodeSummaryResponse(Guid.NewGuid(), Guid.NewGuid(), "Office", "Host", "windows", "x64",
            "0.4.0", "1.0", status, "cert", now.AddHours(1), 4, 4096, 32768, 2, now.AddSeconds(-ageSeconds), [], new Dictionary<string, string>());
        Assert.Equal(expected, OfficePresentation.ReadyForUpgrade(office, active, now));
        Assert.False(OfficePresentation.ReadyForUpgrade(office with { CertificateExpiresAt = now.AddMinutes(-1) }, active, now));
        if (status == "draining" && ageSeconds == 31) Assert.Equal("Offline", OfficePresentation.Status(office, now));
    }
}
