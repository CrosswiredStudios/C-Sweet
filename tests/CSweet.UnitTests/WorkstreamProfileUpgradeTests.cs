using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkstreamProfileUpgradeTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-digest")]
    [InlineData("retired")]
    [InlineData("authority-change")]
    [InlineData("existing-board")]
    [InlineData("stale-workstream")]
    public async Task ApprovedUpgradeRevalidatesTargetAndPreservesProject(string scenario)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var actor = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, IsActive = true };
        var source = new WorkstreamProfileDefinitionRecord { Id = Guid.NewGuid(), Key = "game", Version = 4, DefinitionDigest = "old",
            DefinitionJson = "{\"version\":4,\"authorityPolicyKey\":\"same\",\"orchestration\":{}}", MetadataSchemaJson = "{\"type\":\"object\"}" };
        var target = new WorkstreamProfileDefinitionRecord { Id = Guid.NewGuid(), Key = "game", Version = 5, DefinitionDigest = "new",
            DefinitionJson = "{\"version\":5,\"authorityPolicyKey\":\"same\",\"orchestration\":{\"new\":true}}", MetadataSchemaJson = source.MetadataSchemaJson };
        var stream = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Name = "Game", Outcome = "Build accepted game",
            AccountableManagerOrganizationUserId = actor.Id, ProfileKey = "game", ProfileVersion = 4, ProfileDefinitionDigest = "old",
            ProfileDataJson = "{}", LifecycleStage = "Planning", BudgetAmount = 100, Revision = scenario == "stale-workstream" ? 3 : 2 };
        if (scenario == "retired") target.Status = "Retired";
        if (scenario == "authority-change") target.DefinitionJson = target.DefinitionJson.Replace("same", "different");
        db.AddRange(source, target, stream, actor);
        if (scenario == "existing-board") db.WorkBoards.Add(new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = stream.Id, Name = "Team" });
        await db.SaveChangesAsync();
        var request = new W.WorkstreamChangeProposalRequest(stream.Id, 2, "Upgrade execution workflow",
            JsonSerializer.SerializeToElement(new { profileUpgrade = new { key = "game", version = 5, definitionDigest = scenario == "wrong-digest" ? "other" : "new" } }),
            "Enable reviewed game delivery", "upgrade-1");
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = org, ActionType = "workstream.change.v1", Status = ProposalStatus.Approved,
            PayloadJson = JsonSerializer.Serialize(new { profileDefinitionDigest = "old", payload = request }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var executor = new WorkstreamManagedActionExecutor(db, TimeProvider.System);
        if (scenario == "valid")
        {
            await executor.ExecuteAsync(proposal, actor);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var saved = await db.Workstreams.SingleAsync();
            Assert.Equal(5, saved.ProfileVersion); Assert.Equal("new", saved.ProfileDefinitionDigest); Assert.Equal(3, saved.Revision);
            Assert.Equal("Build accepted game", saved.Outcome); Assert.Equal("Planning", saved.LifecycleStage);
            Assert.Equal(100, saved.BudgetAmount); Assert.Equal(actor.Id, saved.AccountableManagerOrganizationUserId);
        }
        else
        {
            if (scenario == "stale-workstream")
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => executor.ExecuteAsync(proposal, actor));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(proposal, actor));
            db.ChangeTracker.Clear();
            Assert.Equal(4, (await db.Workstreams.SingleAsync()).ProfileVersion);
        }
    }
}
