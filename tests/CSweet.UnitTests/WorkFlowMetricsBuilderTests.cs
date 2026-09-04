using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkFlowMetricsBuilderTests
{
    [Fact]
    public async Task EmptyBoardReportsExplicitSparseConditionsAndNoPhantomThroughput()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var board = Board(organizationId);
        db.WorkBoards.Add(board);
        await db.SaveChangesAsync();
        var end = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var request = new Wire.ReadWorkFlowMetricsRequest(board.Id)
        {
            WindowStart = end.AddDays(-28), WindowEnd = end, CompletedSprintLimit = 6
        };

        var first = await WorkFlowMetricsBuilder.BuildAsync(db, organizationId, request, default);
        var second = await WorkFlowMetricsBuilder.BuildAsync(db, organizationId, request, default);

        Assert.Equal(first.SourceRevision, second.SourceRevision);
        Assert.Equal(0, first.Team.CompletedStageCount);
        Assert.Equal(0, first.Team.ThroughputPerWeek);
        Assert.Contains(Wire.WorkFlowMetricConditionCodes.InsufficientCompletedSprints, first.ConditionCodes);
        Assert.Contains(Wire.WorkFlowMetricConditionCodes.InsufficientAttributedStages, first.ConditionCodes);
        Assert.Contains(Wire.WorkFlowMetricConditionCodes.SparseHistoricalBaseline, first.ConditionCodes);
    }

    [Fact]
    public async Task TeamAndWorkstreamScopeMustOwnTheBoard()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var board = Board(organizationId);
        board.TeamId = Guid.NewGuid();
        board.WorkstreamId = Guid.NewGuid();
        db.WorkBoards.Add(board);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkFlowMetricsBuilder.BuildAsync(
            db, organizationId, new Wire.ReadWorkFlowMetricsRequest(board.Id) { TeamId = Guid.NewGuid() }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkFlowMetricsBuilder.BuildAsync(
            db, organizationId, new Wire.ReadWorkFlowMetricsRequest(board.Id) { WorkstreamId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task PersonalCommitmentsAreNotIncludedInTeamBacklog()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var board = Board(organizationId);
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id,
            BoardColumnId = board.Columns.Single().Id, Kind = WorkItemKind.Task,
            Title = "Team leaf", Description = "", Status = WorkTaskStatus.Backlog,
            Priority = WorkTaskPriority.Medium, BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreWorkTasks.Add(new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = null, BoardColumnId = null,
            CreatedByOrganizationUserId = Guid.NewGuid(),
            Title = "Producer follow-up", Description = "", Status = WorkTaskStatus.Ready,
            Priority = WorkTaskPriority.High, BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var report = await WorkFlowMetricsBuilder.BuildAsync(db, organizationId,
            new Wire.ReadWorkFlowMetricsRequest(board.Id), default);

        Assert.Equal(1, report.Team.PendingDemand);
    }

    private static WorkBoard Board(Guid organizationId)
    {
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Name = "Delivery", Description = "",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        board.Columns.Add(new CSweet.Domain.WorkManagement.WorkBoardColumn
        {
            Id = Guid.NewGuid(), BoardId = board.Id, Name = "Backlog",
            Category = WorkBoardColumnCategory.ToDo, Position = 0
        });
        return board;
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
