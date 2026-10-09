using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static void AssertSavedCleanupOwner(MemoryErasureReceipt receipt, Guid employee)
    {
        using var inventory = JsonDocument.Parse(receipt.InventoryJson);
        Assert.Contains(inventory.RootElement.GetProperty("owners").EnumerateArray(),
            x => x.GetProperty("employeeId").GetGuid() == employee);
    }

    [MemoryPostgresTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task NestedCleanupOwnershipLegacyReceiptRequiresReviewForSharedAndPrivateRoots(bool shared)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        Guid user, inspector, episode;
        if (shared)
        {
            var source = await SeedOrganizationCorrectionAsync(fixture, nestedCorrectionOwnedByViewer: true);
            episode = await WriteOrganizationCorrectionAsync(fixture, source, "Claim");
            inspector = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty); user = source.User;
        }
        else
        {
            var source = await SeedJoblessProposalAsync(fixture);
            user = source.User; episode = source.Episode.Id; inspector = fixture.EmployeeId;
        }
        await using var db = fixture.Context(); var review = ErasureService(fixture, db);
        var preview = await review.GetErasureImpactAsync(fixture.OrganizationId, inspector, episode, user);
        Assert.Null(preview.ApplyBlockedReason);
        await review.EraseSourceAsync(fixture.OrganizationId, inspector, episode, user,
            new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
        var receipt = await db.MemoryErasureReceipts.AsNoTracking().SingleAsync();
        var legacy = JsonSerializer.Deserialize<MemoryErasureReceipt>(JsonSerializer.Serialize(receipt))!;
        legacy.Id = Guid.NewGuid(); legacy.OperationId = Guid.NewGuid();
        var inventory = JsonNode.Parse(legacy.InventoryJson)!.AsObject();
        Assert.True(inventory.Remove("ownershipVersion")); legacy.InventoryJson = inventory.ToJsonString();
        db.MemoryErasureReceipts.Add(legacy); await db.SaveChangesAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => review.GetErasureStatusAsync(
            fixture.OrganizationId, inspector, legacy.OperationId, user));
        Assert.Equal("memory_erasure_ownership_review_required", failure.Message);
    }

    [MemoryPostgresTheory]
    [InlineData("Claim")] [InlineData("EntityClaim")] [InlineData("Procedure")] [InlineData("Block")]
    public async Task NestedCleanupOwnershipKeepsOriginalProposalProducerAfterCorrectionErasure(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture, originalProposalOwnedByViewer: true);
        var correction = await WriteOrganizationCorrectionAsync(fixture, source, kind);
        var inspector = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty);
        await using var db = fixture.Context(); var review = ErasureService(fixture, db);
        var preview = await review.GetErasureImpactAsync(fixture.OrganizationId, inspector, correction, source.User);
        Assert.Null(preview.ApplyBlockedReason);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        Assert.Equal("completed", (await review.EraseSourceAsync(fixture.OrganizationId, inspector, correction, source.User, request)).Status);
        var retained = await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(source.Original.Partition, source.Original.Id);
        Assert.NotNull(retained); Assert.Equal(source.Original.SourceFingerprint, retained.SourceFingerprint);
        AssertSavedCleanupOwner(await db.MemoryErasureReceipts.AsNoTracking().SingleAsync(), source.ViewingEmployee);
        await db.CoreOrganizationUsers.Where(x => x.Id == source.ViewingEmployee)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetErasureStatusAsync(fixture.OrganizationId,
            inspector, request.OperationId, source.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.EraseSourceAsync(fixture.OrganizationId,
            inspector, correction, source.User, request));
    }

    [MemoryPostgresTheory]
    [InlineData("Claim")] [InlineData("EntityClaim")] [InlineData("Procedure")] [InlineData("Block")]
    public async Task NestedCleanupOwnershipPersistsEveryCorrectionProducerForReplayAndStatus(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture, nestedCorrectionOwnedByViewer: true);
        var correction = await WriteOrganizationCorrectionAsync(fixture, source, kind);
        var inspector = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty);
        await using var db = fixture.Context(); var review = ErasureService(fixture, db);
        var preview = await review.GetErasureImpactAsync(fixture.OrganizationId, inspector, correction, source.User);
        Assert.Null(preview.ApplyBlockedReason);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        Assert.Equal("completed", (await review.EraseSourceAsync(fixture.OrganizationId, inspector, correction, source.User, request)).Status);
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, correction));
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, source.Reviewed.Id));
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(source.Original.Partition, source.Original.Id));
        var receipt = await db.MemoryErasureReceipts.AsNoTracking().SingleAsync();
        AssertSavedCleanupOwner(receipt, fixture.EmployeeId); AssertSavedCleanupOwner(receipt, source.ViewingEmployee);
        Assert.True((await review.EraseSourceAsync(fixture.OrganizationId, inspector, correction, source.User, request)).WasReplay);
        await db.CoreOrganizationUsers.Where(x => x.Id == source.ViewingEmployee)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetErasureStatusAsync(fixture.OrganizationId,
            inspector, request.OperationId, source.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.EraseSourceAsync(fixture.OrganizationId,
            inspector, correction, source.User, request));
        Assert.Single(await db.MemoryErasureReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Claim", "root", false)] [InlineData("Claim", "nested", false)]
    [InlineData("EntityClaim", "root", false)] [InlineData("EntityClaim", "nested", false)]
    [InlineData("Procedure", "root", false)] [InlineData("Procedure", "nested", false)]
    [InlineData("Block", "root", false)] [InlineData("Block", "nested", false)]
    [InlineData("Claim", "root", true)] [InlineData("Claim", "nested", true)]
    [InlineData("EntityClaim", "root", true)] [InlineData("EntityClaim", "nested", true)]
    [InlineData("Procedure", "root", true)] [InlineData("Procedure", "nested", true)]
    [InlineData("Block", "root", true)] [InlineData("Block", "nested", true)]
    public async Task NestedCleanupOwnershipPreservationJobsKeepRootAndNestedProducerAuthority(string kind, string revoked, bool completed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture, nestedCorrectionOwnedByViewer: true);
        var correctionId = await WriteOrganizationCorrectionAsync(fixture, source, kind);
        var proposal = await PrepareGenericProposalAsync(fixture);
        proposal = proposal with { Partition = source.Reviewed.Partition, Scope = source.Reviewed.Scope };
        await fixture.Store.AppendEpisodeAsync(proposal);
        var now = DateTimeOffset.UtcNow;
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(), proposal.Partition, correctionId, "Preserved correction", "Use reviewed instructions",
            null, 1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, now, null, now)
            { SourceEpisodeIds = [proposal.Id, correctionId] });
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards);
        var recovery = IngestionRecovery(fixture, db);
        var pending = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, proposal.Id, source.User);
        Assert.True(pending.CanQueue, pending.BlockedReason);
        await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, proposal.Id, source.User,
            new(Guid.NewGuid(), pending.Revision, pending.EvidenceToken, pending.RequiredReconciliationPolicy));
        if (completed) Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        db.ChangeTracker.Clear();
        var inspector = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty);
        var review = ErasureService(fixture, db);
        var inventory = await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(proposal.Partition, proposal.Id);
        Assert.DoesNotContain(inventory.Targets, x => x.Kind == MemoryErasureKind.Episode && x.Id == correctionId);
        var preview = await review.GetErasureImpactAsync(fixture.OrganizationId, inspector, proposal.Id, source.User);
        Assert.Null(preview.ApplyBlockedReason); Assert.Equal(1, preview.Execution!.ExtractionJobs);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        var result = await review.EraseSourceAsync(fixture.OrganizationId, inspector, proposal.Id, source.User, request);
        Assert.Equal("completed", result.Status); Assert.Equal(1, result.ClearedJobs);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToArrayAsync());
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(proposal.Partition, correctionId));
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(proposal.Partition, source.Reviewed.Id));
        var receipt = await db.MemoryErasureReceipts.AsNoTracking().SingleAsync();
        AssertSavedCleanupOwner(receipt, fixture.EmployeeId); AssertSavedCleanupOwner(receipt, source.ViewingEmployee);
        Assert.Equal("completed", (await review.GetErasureStatusAsync(fixture.OrganizationId, inspector, request.OperationId, source.User)).Status);
        await db.CoreOrganizationUsers.Where(x => x.Id == (revoked == "root" ? fixture.EmployeeId : source.ViewingEmployee))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetErasureStatusAsync(fixture.OrganizationId,
            inspector, request.OperationId, source.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.EraseSourceAsync(fixture.OrganizationId,
            inspector, proposal.Id, source.User, request));
    }
}
