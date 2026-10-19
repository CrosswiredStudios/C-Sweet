using CSweet.Contracts.WorkManagement;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class DeliveryForecastTests
{
    [Fact]
    public async Task ReportUsesCurrentScopeWithoutDatesAndRefreshesWhenEstimatesChange()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var org = Guid.NewGuid(); var board = Guid.NewGuid(); var sprint = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization { Id = org, Name = "Forecast test", CreatedAt = Now, UpdatedAt = Now });
        db.WorkBoards.Add(new WorkBoard { Id = board, OrganizationId = org, Name = "Delivery", CreatedAt = Now, UpdatedAt = Now });
        db.WorkSprints.Add(new WorkSprint { Id = sprint, OrganizationId = org, BoardId = board, Name = "Increment", Status = WorkSprintStatus.Active, StartedAt = Now });
        var work = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board, SprintId = sprint,
            Title = "Current task", Status = WorkTaskStatus.Ready, EstimatePoints = 10 };
        var samples = new[] { Sample(2, 5), Sample(6, 5) };
        foreach (var sample in samples) { sample.Id = Guid.NewGuid(); sample.SprintId = Guid.NewGuid(); sample.OrganizationId = org; sample.BoardId = board; }
        db.WorkSprintSnapshots.AddRange(samples);
        db.CoreWorkTasks.AddRange(work,
            new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board, SprintId = sprint, Title = "Cancelled", Status = WorkTaskStatus.Cancelled, EstimatePoints = 99 },
            new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board, SprintId = sprint, Title = "Parent", IsExecutable = false, EstimatePoints = 99 });
        await db.SaveChangesAsync();
        var report = await WorkSprintReportBuilder.BuildAsync(db, org, board, default);
        Assert.Equal(10, report.ActiveForecast!.RemainingPoints);
        Assert.NotNull(report.ActiveForecast.LatestCompletionAt);
        work.EstimatePoints = null;
        await db.SaveChangesAsync();
        var unknown = await WorkSprintReportBuilder.BuildAsync(db, org, board, default);
        Assert.Equal(1, unknown.ActiveForecast!.UnestimatedItemCount);
        Assert.Null(unknown.ActiveForecast.LatestCompletionAt);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static WorkSprintSnapshot Sample(double hours, decimal points) => new()
    {
        StartedAt = Now.AddDays(-2).AddHours(-hours), CompletedAt = Now.AddDays(-2), CompletedPoints = points
    };
    private static WorkSprintForecastResponse Forecast(decimal remaining) => new(Guid.NewGuid(), "Increment", remaining, 5, 1, false);

    [Fact]
    public void UsesActualElapsedHistoryAndRemainingWorkWithoutPlannedDates()
    {
        var result = DeliveryForecast.WithCompletionRange(Forecast(10), [Sample(2, 5), Sample(6, 5)], 0, Now);
        Assert.Equal(Now.AddHours(4), result.EarliestCompletionAt);
        Assert.Equal(Now.AddHours(12), result.LatestCompletionAt);
        Assert.Equal(2, result.HistoricalSampleCount);
        Assert.Contains("not a commitment", result.Basis);
        var revised = DeliveryForecast.WithCompletionRange(Forecast(5), [Sample(2, 5), Sample(6, 5)], 0, Now);
        Assert.Equal(Now.AddHours(6), revised.LatestCompletionAt);
    }

    [Fact]
    public void UnestimatedWorkAndSparseHistoryDoNotProduceInventedDates()
    {
        var unknown = DeliveryForecast.WithCompletionRange(Forecast(0), [Sample(2, 5), Sample(6, 5)], 1, Now);
        Assert.Null(unknown.LatestCompletionAt);
        Assert.Equal(1, unknown.UnestimatedItemCount);
        var sparse = DeliveryForecast.WithCompletionRange(Forecast(5), [Sample(2, 5), Sample(-1, 5), Sample(2, 0)], 0, Now);
        Assert.Null(sparse.EarliestCompletionAt);
        Assert.Equal(1, sparse.HistoricalSampleCount);
    }

    [Fact]
    public void CompletedScopeDoesNotClaimAcceptanceOrRelease()
    {
        var result = DeliveryForecast.WithCompletionRange(Forecast(0), [], 0, Now);
        Assert.Equal(Now, result.LatestCompletionAt);
        Assert.Contains("gates still apply", result.Basis);
    }

    [Fact]
    public void UnresolvedApprovalDoesNotGetAFabricatedResolutionTime()
    {
        var result = DeliveryForecast.WithCompletionRange(Forecast(10) with { BlockingItemCount = 1 },
            [Sample(2, 5), Sample(6, 5)], 0, Now);
        Assert.Null(result.LatestCompletionAt);
        Assert.Contains("unresolved blockers", result.Basis);
    }
}
