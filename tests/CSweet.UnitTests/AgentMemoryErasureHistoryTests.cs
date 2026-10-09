using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static MemoryErasureReceipt CloneErasureReceipt(MemoryErasureReceipt source) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = source.OrganizationId, EmployeeId = source.EmployeeId,
        EpisodeId = source.EpisodeId, OperationId = Guid.NewGuid(), ActorApplicationUserId = source.ActorApplicationUserId,
        ActorOrganizationUserId = source.ActorOrganizationUserId, RequestHash = source.RequestHash,
        InventoryJson = source.InventoryJson, CreatedAt = source.CreatedAt, ErasedRecords = source.ErasedRecords,
        ErasedRevisions = source.ErasedRevisions, ClearedJobs = source.ClearedJobs,
        ClearedWorks = source.ClearedWorks, ClearedDiagnosticTurns = source.ClearedDiagnosticTurns
    };

    private static async Task<(Guid User, MemoryErasureReceipt Receipt)> EraseHistorySourceAsync(DurabilityFixture fixture)
    {
        var source = await SeedJoblessProposalAsync(fixture);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, source.Episode.Id, source.User);
        Assert.Null(preview.ApplyBlockedReason);
        await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, source.Episode.Id, source.User,
            new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
        return (source.User, await db.MemoryErasureReceipts.AsNoTracking().SingleAsync());
    }

    [MemoryPostgresFact]
    public async Task ErasureHistoryFindsOnlyOriginalActorsOwnOperationsAfterSourceDeletion()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await EraseHistorySourceAsync(fixture);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var otherUser = CloneErasureReceipt(source.Receipt); otherUser.ActorApplicationUserId = Guid.NewGuid();
        var otherActor = CloneErasureReceipt(source.Receipt); otherActor.ActorOrganizationUserId = Guid.NewGuid();
        var otherEmployee = CloneErasureReceipt(source.Receipt); otherEmployee.EmployeeId = Guid.NewGuid();
        var otherTenant = CloneErasureReceipt(source.Receipt); otherTenant.OrganizationId = Guid.NewGuid();
        db.MemoryErasureReceipts.AddRange(otherUser, otherActor, otherEmployee, otherTenant); await db.SaveChangesAsync();
        var page = await service.ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User);
        Assert.Null(page.NextBeforeReceiptId); var item = Assert.Single(page.Items);
        Assert.Equal(source.Receipt.OperationId, item.OperationId); Assert.Equal("available", item.Availability);
        Assert.Equal("completed", (await service.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, item.OperationId, source.User)).Status);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain(source.Receipt.EpisodeId.ToString("D"), json);
        Assert.DoesNotContain(fixture.EmployeeId.ToString("D"), json);
        Assert.DoesNotContain("Inventory", json); Assert.DoesNotContain("ErasedRecords", json);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListErasureOperationsAsync(fixture.OrganizationId,
            fixture.EmployeeId, source.User, otherUser.Id));
    }

    [MemoryPostgresFact]
    public async Task ErasureHistoryPaginatesStableTiesWithoutRepeatingNewerRowsAndRechecksRootAuthority()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await EraseHistorySourceAsync(fixture);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var copies = Enumerable.Range(1, 5).Select(index =>
        {
            var copy = CloneErasureReceipt(source.Receipt);
            copy.Id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}");
            copy.CreatedAt = source.Receipt.CreatedAt.AddMinutes(1); return copy;
        }).ToArray();
        db.MemoryErasureReceipts.AddRange(copies); await db.SaveChangesAsync();
        var first = await service.ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User, limit: 2);
        Assert.Equal(new[] { copies[4].OperationId, copies[3].OperationId }, first.Items.Select(x => x.OperationId));
        Assert.Equal(copies[3].Id, first.NextBeforeReceiptId);
        var newer = CloneErasureReceipt(source.Receipt); newer.CreatedAt = copies[0].CreatedAt.AddMinutes(1);
        db.MemoryErasureReceipts.Add(newer); await db.SaveChangesAsync();
        var second = await service.ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User, first.NextBeforeReceiptId, 2);
        Assert.Equal(new[] { copies[2].OperationId, copies[1].OperationId }, second.Items.Select(x => x.OperationId));
        var last = await service.ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User, second.NextBeforeReceiptId, 2);
        Assert.Equal(new[] { copies[0].OperationId, source.Receipt.OperationId }, last.Items.Select(x => x.OperationId));
        Assert.Null(last.NextBeforeReceiptId);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListErasureOperationsAsync(fixture.OrganizationId,
            fixture.EmployeeId, source.User, first.NextBeforeReceiptId));
    }

    [MemoryPostgresTheory]
    [InlineData("legacy", "ownership-review-required")]
    [InlineData("empty-owners", "ownership-review-required")]
    [InlineData("invalid-json", "evidence-review-required")]
    [InlineData("null", "evidence-review-required")]
    [InlineData("missing-runtimes", "evidence-review-required")]
    [InlineData("null-runtime", "evidence-review-required")]
    [InlineData("missing-runtime-date", "evidence-review-required")]
    [InlineData("duplicate-runtime", "evidence-review-required")]
    [InlineData("empty-owner-id", "evidence-review-required")]
    [InlineData("too-many-runtimes", "evidence-review-required")]
    public async Task ErasureHistoryClassifiesUnverifiableInventoriesWithoutReturningContent(string defect, string availability)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await EraseHistorySourceAsync(fixture);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var copy = CloneErasureReceipt(source.Receipt); copy.CreatedAt = source.Receipt.CreatedAt.AddMinutes(1);
        var inventory = JsonNode.Parse(copy.InventoryJson)!.AsObject();
        switch (defect)
        {
            case "legacy": Assert.True(inventory.Remove("ownershipVersion")); break;
            case "empty-owners": inventory["owners"] = new JsonArray(); break;
            case "invalid-json": copy.InventoryJson = "private-memory invalid JSON"; break;
            case "null": copy.InventoryJson = "null"; break;
            case "missing-runtimes": Assert.True(inventory.Remove("pendingRuntimes")); break;
            case "null-runtime": inventory["pendingRuntimes"] = new JsonArray((JsonNode?)null); break;
            case "missing-runtime-date": inventory["pendingRuntimes"] = new JsonArray(new JsonObject
                { ["id"] = Guid.NewGuid().ToString("D") }); break;
            case "duplicate-runtime":
                var runtime = new JsonObject { ["id"] = Guid.NewGuid().ToString("D"),
                    ["requestedAt"] = DateTimeOffset.UtcNow.ToString("O") };
                inventory["pendingRuntimes"] = new JsonArray(runtime, runtime.DeepClone()); break;
            case "empty-owner-id": inventory["owners"]![0]!["employeeId"] = Guid.Empty.ToString("D"); break;
            case "too-many-runtimes": inventory["pendingRuntimes"] = new JsonArray(Enumerable.Range(0, 1025)
                .Select(_ => (JsonNode)new JsonObject { ["id"] = Guid.NewGuid().ToString("D"),
                    ["requestedAt"] = DateTimeOffset.UtcNow.ToString("O") }).ToArray()); break;
        }
        if (defect is not ("invalid-json" or "null")) copy.InventoryJson = inventory.ToJsonString();
        db.MemoryErasureReceipts.Add(copy); await db.SaveChangesAsync();
        var item = Assert.Single((await service.ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User, limit: 1)).Items);
        Assert.Equal(copy.OperationId, item.OperationId); Assert.Equal(availability, item.Availability);
        Assert.DoesNotContain("private-memory", JsonSerializer.Serialize(item));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetErasureStatusAsync(fixture.OrganizationId,
            fixture.EmployeeId, copy.OperationId, source.User));
        Assert.Equal(availability == "ownership-review-required" ? "memory_erasure_ownership_review_required" :
            "memory_erasure_evidence_review_required", failure.Message);
        Assert.Equal(copy.InventoryJson, (await db.MemoryErasureReceipts.AsNoTracking().SingleAsync(x => x.Id == copy.Id)).InventoryJson);
    }

    [MemoryPostgresFact]
    public async Task ErasureHistoryRechecksNestedRightsAndRestorationBeforeAllowingStatus()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture, nestedCorrectionOwnedByViewer: true);
        var episode = await WriteOrganizationCorrectionAsync(fixture, source, "EntityClaim");
        var inspector = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, inspector, episode, source.User);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        await service.EraseSourceAsync(fixture.OrganizationId, inspector, episode, source.User, request);
        Assert.Equal("available", Assert.Single((await service.ListErasureOperationsAsync(fixture.OrganizationId, inspector, source.User)).Items).Availability);
        await db.CoreOrganizationUsers.Where(x => x.Id == source.ViewingEmployee)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)null));
        var page = await service.ListErasureOperationsAsync(fixture.OrganizationId, inspector, source.User);
        Assert.Equal("permission-required", Assert.Single(page.Items).Availability);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain(episode.ToString("D"), json); Assert.DoesNotContain(source.ViewingEmployee.ToString("D"), json);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetErasureStatusAsync(fixture.OrganizationId, inspector, request.OperationId, source.User));
        await db.CoreOrganizationUsers.Where(x => x.Id == source.ViewingEmployee)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)fixture.HumanId));
        Assert.Equal("available", Assert.Single((await service.ListErasureOperationsAsync(fixture.OrganizationId, inspector, source.User)).Items).Availability);
    }

    [MemoryPostgresTheory]
    [InlineData(0)] [InlineData(21)] [InlineData(-1)]
    public async Task ErasureHistoryRejectsUnboundedPageSizes(int limit)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await EraseHistorySourceAsync(fixture);
        await using var db = fixture.Context();
        await Assert.ThrowsAsync<ArgumentException>(() => ErasureService(fixture, db)
            .ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User, limit: limit));
    }

    [MemoryPostgresFact]
    public async Task ErasureHistoryMigrationPreservesSavedOperationIdentityAndEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await EraseHistorySourceAsync(fixture);
        await using var db = fixture.Context();
        var generator = db.GetService<IMigrationsSqlGenerator>(); var migration = new MemoryErasureActorHistory();
        // EnsureCreated includes the current index. Rehearse the index-only upgrade
        // from the preceding table shape with a real saved cleanup receipt.
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var saved = await db.MemoryErasureReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(source.Receipt.Id, saved.Id); Assert.Equal(source.Receipt.OperationId, saved.OperationId);
        Assert.Equal(source.Receipt.InventoryJson, saved.InventoryJson);
        var page = await ErasureService(fixture, db).ListErasureOperationsAsync(fixture.OrganizationId, fixture.EmployeeId, source.User);
        Assert.Equal(saved.OperationId, Assert.Single(page.Items).OperationId);
    }
}
