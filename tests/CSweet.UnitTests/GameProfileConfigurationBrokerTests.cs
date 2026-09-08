using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Fact]
    public async Task UpdatedGameProfileConfiguresEmptyBoardThroughBrokerAndReplaysWithoutDuplicates()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var setup = SeedInstallation(db);
        var manager = db.CoreOrganizationUsers.Local.Single();
        using var fixture = typeof(GameDeliveryProfileHostTests).Assembly.GetManifestResourceStream(
            "CSweet.UnitTests.Fixtures.video-game-production.v2.5.json")!;
        using var document = JsonDocument.Parse(fixture);
        var digest = new string('a',64);
        var streamId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = "video-game-production.v2", Version = 5,
            DefinitionDigest = digest, DefinitionJson = document.RootElement.GetRawText(), MetadataSchemaJson = "{}", Status = "Active" });
        db.Workstreams.Add(new() { Id = streamId, OrganizationId = setup.OrganizationId, Name = "Game",
            ProfileKey = "video-game-production.v2", ProfileVersion = 5, ProfileDefinitionDigest = digest,
            AccountableManagerOrganizationUserId = manager.Id });
        db.WorkBoards.Add(new() { Id = boardId, OrganizationId = setup.OrganizationId, WorkstreamId = streamId,
            ManagerOrganizationUserId = manager.Id, Name = "Game team", Revision = 1,
            Columns = [Column("To Do", WorkBoardColumnCategory.ToDo, 0), Column("Done", WorkBoardColumnCategory.Done, 1)] });
        Grant(db, setup, WorkOrchestrationActions.ConfigureProfile, GrantScopeKind.Board, boardId);
        Grant(db, setup, WorkBoardActions.ConfigureColumns, GrantScopeKind.Board, boardId);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var request = new Wire.ConfigureProfileOrchestrationRequest(streamId, boardId, 1, digest, "configure-game-profile");
        var result = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.ConfigureProfile),
            WorkOrchestrationActions.ConfigureProfile, request);
        Assert.True(result.Succeeded, result.Error);
        db.ChangeTracker.Clear();
        var policy = await db.WorkOrchestrationPolicies.SingleAsync();
        Assert.NotNull(policy.PublishedRevisionId);
        var revision = await db.WorkOrchestrationPolicyRevisions.Include(x => x.Stages).SingleAsync();
        Assert.Equal(policy.PublishedRevisionId, revision.Id);
        Assert.Contains(revision.Stages, x => x.Key == "technical-review");
        Assert.Contains(revision.Stages, x => x.Key == "quality");
        Assert.Contains(revision.Stages, x => x.Key == "governed-merge");
        var columnCount = await db.WorkBoardColumns.CountAsync();
        Assert.Equal(document.RootElement.GetProperty("boardWorkflow").GetProperty("columns").GetArrayLength(), columnCount);
        var replay = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.ConfigureProfile),
            WorkOrchestrationActions.ConfigureProfile, request);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal(columnCount, await db.WorkBoardColumns.CountAsync());
        Assert.Single(await db.WorkOrchestrationPolicyRevisions.ToListAsync());
    }
}
