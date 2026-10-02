using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CoordinationWakeTests
{
    [Fact]
    public async Task CompletedSessionWakesTheParticipantsWaitingCommitmentForThatBoard()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var organization = Guid.NewGuid();
        var producer = Guid.NewGuid();
        var engineer = Guid.NewGuid();
        var board = Guid.NewGuid();
        var otherBoard = Guid.NewGuid();
        var session = new AgentCoordinationSession
        {
            Id = Guid.NewGuid(), OrganizationId = organization, ConversationId = Guid.NewGuid(),
            SourceKind = "Board", SourceBoardId = board,
            InitiatorOrganizationUserId = producer, InitiatorInstallationId = Guid.NewGuid(),
            TargetOrganizationUserId = engineer, TargetInstallationId = Guid.NewGuid(),
            Subject = "game-engineer sprint estimate and capacity", Objective = "Estimate",
            IdempotencyKey = "estimate", CreatedAt = now, UpdatedAt = now
        };
        WorkTask Waiting(Guid owner, Guid boardId) => new()
        {
            Id = Guid.NewGuid(), OrganizationId = organization, AssignedEmployeeId = owner,
            Title = "Collect estimates", Status = WorkTaskStatus.Running,
            NextReviewAt = now.AddMinutes(15), WaitingReason = "Role estimation is pending.",
            PersonalWorkContextJson = $$"""{"workstreamId":null,"boardId":"{{boardId}}","coordinationSessionId":null}""",
            CreatedAt = now, UpdatedAt = now
        };
        var dependent = Waiting(producer, board);
        var unrelatedBoard = Waiting(producer, otherBoard);
        var bystander = Waiting(Guid.NewGuid(), board);
        db.AddRange(session, dependent, unrelatedBoard, bystander);
        await db.SaveChangesAsync();

        session.Status = AgentCoordinationStatus.Completed;
        session.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.True(dependent.NextReviewAt <= DateTimeOffset.UtcNow);
        Assert.True(unrelatedBoard.NextReviewAt > DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.True(bystander.NextReviewAt > DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Fact]
    public async Task NonTerminalSessionChangesDoNotWakeCommitments()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var producer = Guid.NewGuid();
        var board = Guid.NewGuid();
        var session = new AgentCoordinationSession
        {
            Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), ConversationId = Guid.NewGuid(),
            SourceKind = "Board", SourceBoardId = board,
            InitiatorOrganizationUserId = producer, InitiatorInstallationId = Guid.NewGuid(),
            TargetOrganizationUserId = Guid.NewGuid(), TargetInstallationId = Guid.NewGuid(),
            Subject = "estimate", Objective = "Estimate", IdempotencyKey = "estimate-2", CreatedAt = now, UpdatedAt = now
        };
        var waiting = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = session.OrganizationId, AssignedEmployeeId = producer,
            Title = "Collect estimates", Status = WorkTaskStatus.Running, NextReviewAt = now.AddMinutes(15),
            WaitingReason = "pending", PersonalWorkContextJson = $$"""{"boardId":"{{board}}"}""",
            CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(session, waiting);
        await db.SaveChangesAsync();

        session.NextTurnOrdinal = 2;
        await db.SaveChangesAsync();

        Assert.True(waiting.NextReviewAt > DateTimeOffset.UtcNow.AddMinutes(10));
    }
}
