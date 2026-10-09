using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record OrganizationCorrectionFixture(Guid User, Guid ViewingEmployee,
        MemoryEpisode Original, MemoryEpisode Reviewed);

    private static async Task<OrganizationCorrectionFixture> SeedOrganizationCorrectionAsync(
        DurabilityFixture fixture, string receiptState = "valid", bool nestedCorrectionOwnedByViewer = false,
        bool originalProposalOwnedByViewer = false)
    {
        var (user, _) = await SeedRecoveryAsync(fixture);
        var viewingEmployee = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty);
        var audience = EmployeeMemoryNamespaces.Organization(fixture.OrganizationId.ToString("D"), "csweet");
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        var original = new MemoryEpisode(Guid.NewGuid(), audience.Partition, audience.Scope,
            "Use concise operating instructions", "text/plain",
            new("user", Guid.NewGuid().ToString("D"), fixture.HumanId.ToString("D")), "original", now, now);
        if (originalProposalOwnedByViewer)
        {
            await using var ownerDb = fixture.Context();
            var installation = (await ownerDb.CoreOrganizationUsers.SingleAsync(x => x.Id == viewingEmployee)).AgentInstallationId!.Value;
            original = original with { Source = new("agent-proposal", original.Id.ToString("D"), viewingEmployee.ToString("D")),
                Sensitivity = MemorySensitivity.Personal,
                Metadata = new Dictionary<string, string> { ["installationId"] = installation.ToString("D"), ["employeeId"] = viewingEmployee.ToString("D") } };
        }
        if (nestedCorrectionOwnedByViewer)
        {
            // The real correction writer flattens one prior correction level. Put the
            // other producer below that level so its correction remains in the closure.
            var leaf = original with { Id = Guid.NewGuid() };
            await fixture.Store.AppendEpisodeAsync(leaf);
            var nestedOperation = Guid.NewGuid(); var nestedRecord = Guid.NewGuid();
            var nestedEvidence = await ((PostgreSqlMemoryStore)fixture.Store).CaptureCorrectionEvidenceAsync(
                audience.Partition, nestedOperation, [leaf.Id]);
            original = original with { Source = new("user", nestedOperation.ToString("D"), fixture.HumanId.ToString("D")),
                CorrectionEvidence = nestedEvidence,
                OperationalReferences = [new("memory-claim", nestedRecord.ToString("D"), "1")] };
            await using var nestedDb = fixture.Context();
            nestedDb.MemoryReviewReceipts.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeId = viewingEmployee,
                ActorApplicationUserId = user, ActorOrganizationUserId = fixture.HumanId, OperationId = nestedOperation,
                RecordKind = "Claim", Action = "correct", MemoryId = nestedRecord, PreviousRevision = 1,
                ResultMemoryId = Guid.NewGuid(), ResultRevision = 1, RequestHash = "trusted-nested-review", CreatedAt = now
            });
            await nestedDb.SaveChangesAsync();
        }
        await fixture.Store.AppendEpisodeAsync(original);
        original = (await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(audience.Partition, original.Id))!;
        var operation = Guid.NewGuid(); var record = Guid.NewGuid();
        var ancestry = await ((PostgreSqlMemoryStore)fixture.Store).CaptureCorrectionEvidenceAsync(
            audience.Partition, operation, [original.Id]);
        // A previously committed, trusted human correction snapshot. Its partition has
        // no AgentId; the immutable review receipt identifies the responsible employee.
        var reviewed = new MemoryEpisode(Guid.NewGuid(), audience.Partition, audience.Scope,
            "Use detailed operating instructions", "text/plain",
            new("user", operation.ToString("D"), fixture.HumanId.ToString("D")), "reviewed", now, now,
            OperationalReferences: [new("memory-claim", record.ToString("D"), "1")])
            { CorrectionEvidence = ancestry, Sensitivity = original.Sensitivity };
        await fixture.Store.AppendEpisodeAsync(reviewed);
        await using var db = fixture.Context();
        if (receiptState != "missing")
        {
            db.MemoryReviewReceipts.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                EmployeeId = fixture.EmployeeId,
                ActorApplicationUserId = user, ActorOrganizationUserId = receiptState == "wrong-reviewer"
                    ? viewingEmployee : fixture.HumanId,
                OperationId = receiptState == "wrong-operation" ? Guid.NewGuid() : operation,
                RecordKind = "Claim", Action = "correct", MemoryId = receiptState == "wrong-reference" ? Guid.NewGuid() : record,
                PreviousRevision = 1, ResultMemoryId = Guid.NewGuid(), ResultRevision = 1,
                RequestHash = "trusted-prior-review", CreatedAt = now
            });
            await db.SaveChangesAsync();
        }
        return new(user, viewingEmployee, original, (await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(audience.Partition, reviewed.Id))!);
    }

    private static async Task<Guid> WriteOrganizationCorrectionAsync(DurabilityFixture fixture,
        OrganizationCorrectionFixture source, string kind)
    {
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        if (kind is "Claim" or "EntityClaim")
        {
            var subject = new MemoryEntity(Guid.NewGuid(), source.Reviewed.Partition, "policy", "Instructions", [], null,
                false, now, now) { SourceEpisodeIds = [source.Reviewed.Id], Sensitivity = MemorySensitivity.Internal };
            await fixture.Store.UpsertEntityAsync(subject);
            MemoryEntity? replacement = null; MemoryEntity? previous = null;
            if (kind == "EntityClaim")
            {
                previous = subject with { Id = Guid.NewGuid(), CanonicalName = "Detailed instructions" };
                replacement = subject with { Id = Guid.NewGuid(), CanonicalName = "Bulleted instructions" };
                await fixture.Store.UpsertEntityAsync(previous); await fixture.Store.UpsertEntityAsync(replacement);
            }
            var claim = new MemoryClaim(Guid.NewGuid(), source.Reviewed.Partition, source.Reviewed.Id, subject.Id,
                "format", previous?.Id, previous is null ? "detailed" : null, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending,
                MemorySensitivity.Internal, 1, 1, now, null, now) { SourceEpisodeIds = [source.Reviewed.Id] };
            await fixture.Store.WriteClaimAsync(claim);
            var preview = await review.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, source.User);
            Assert.True(preview.CanCorrect);
            ReviewMemoryClaimRequest request;
            if (replacement is null) request = new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "bulleted");
            else
            {
                var choice = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId,
                    fixture.EmployeeId, claim.Id, source.User, "bulleted"));
                Assert.Equal(replacement.Id, choice.EntityId);
                request = new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct",
                    ReplacementEntity: new(replacement.Id, choice.EvidenceToken));
            }
            var result = await review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, source.User, request);
            return (await fixture.Store.GetClaimAsync(result.ResultClaimId))!.EpisodeId;
        }
        if (kind == "Procedure")
        {
            var procedure = new ProceduralMemory(Guid.NewGuid(), source.Reviewed.Partition, source.Reviewed.Id,
                "Instructions", "Use detailed instructions", null, 1, MemoryTrustTier.AgentInference,
                MemoryConfirmationState.Pending, now, null, now)
                { SourceEpisodeIds = [source.Reviewed.Id] };
            await fixture.Store.WriteProcedureAsync(procedure);
            var preview = await review.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, source.User);
            Assert.True(preview.CanCorrect);
            var result = await review.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, source.User,
                new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", new("Instructions", "Use bulleted instructions", null)));
            return (await fixture.Store.ExportAsync(source.Reviewed.Partition)).Procedures
                .Single(x => x.Id == result.ResultProcedureId).EpisodeId;
        }
        var block = new MemoryBlock(Guid.NewGuid(), source.Reviewed.Partition, "Instructions", "Use detailed instructions",
            1, 500, true, MemoryTrustTier.AgentInference, now)
            { SourceEpisodeIds = [source.Reviewed.Id], Sensitivity = MemorySensitivity.Internal, Confirmation = MemoryConfirmationState.Pending };
        await fixture.Store.WriteBlockAsync(block);
        var corePreview = await review.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, source.User);
        Assert.True(corePreview.CanCorrect);
        await review.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, source.User,
            new(Guid.NewGuid(), corePreview.Revision, corePreview.EvidenceToken, "correct", new("Use bulleted instructions", true)));
        return Assert.Single((await fixture.Store.ExportAsync(source.Reviewed.Partition)).Blocks.Single(x => x.Id == block.Id)
            .SourceEpisodeIds, x => x != source.Reviewed.Id);
    }

    [MemoryPostgresTheory]
    [InlineData("Claim")]
    [InlineData("Procedure")]
    [InlineData("Block")]
    public async Task OrganizationCorrectionErasureUsesReviewOwnerAndReauthorizesItAfterCleanup(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture);
        var correction = await WriteOrganizationCorrectionAsync(fixture, source, kind);
        var episode = (await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, correction))!;
        Assert.Null(episode.Partition.AgentId);
        Assert.NotNull(episode.CorrectionEvidence);
        await using var db = fixture.Context(); var erasure = ErasureService(fixture, db);
        // The organization inspector may be opened through another managed employee.
        // That route must not replace the actual correction producer's authority.
        var preview = await erasure.GetErasureImpactAsync(fixture.OrganizationId, source.ViewingEmployee, correction, source.User);
        Assert.Null(preview.ApplyBlockedReason);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        var result = await erasure.EraseSourceAsync(fixture.OrganizationId, source.ViewingEmployee, correction, source.User, request);
        Assert.Equal("completed", result.Status);
        Assert.Null(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, correction));
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, source.Original.Id));
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, source.Reviewed.Id));
        Assert.True((await erasure.EraseSourceAsync(fixture.OrganizationId, source.ViewingEmployee, correction, source.User, request)).WasReplay);
        Assert.Equal("completed", (await erasure.GetErasureStatusAsync(fixture.OrganizationId, source.ViewingEmployee,
            request.OperationId, source.User)).Status);

        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => erasure.GetErasureStatusAsync(
            fixture.OrganizationId, source.ViewingEmployee, request.OperationId, source.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => erasure.EraseSourceAsync(
            fixture.OrganizationId, source.ViewingEmployee, correction, source.User, request));
    }

    [MemoryPostgresTheory]
    [InlineData("held")]
    [InlineData("suppressed")]
    [InlineData("expired")]
    public async Task OrganizationCorrectionCleanupPreservesIndependentRetentionPolicy(string policy)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture);
        var correction = await WriteOrganizationCorrectionAsync(fixture, source, "Claim");
        await using var db = fixture.Context();
        if (policy == "held")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id={source.Original.Id}");
        else if (policy == "suppressed")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['isSuppressed'],'true'::jsonb) WHERE id={source.Original.Id}");
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['expiresAt'],to_jsonb({DateTimeOffset.UtcNow.AddMinutes(-1)})) WHERE id={source.Original.Id}");
        var erasure = ErasureService(fixture, db);
        var preview = await erasure.GetErasureImpactAsync(fixture.OrganizationId, source.ViewingEmployee, correction, source.User);
        if (policy == "held")
        {
            Assert.Equal("memory_legal_hold_prevents_deletion", preview.ApplyBlockedReason);
            Assert.Null(preview.EvidenceToken);
            Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, correction));
        }
        else
        {
            Assert.False(MemoryProvenance.IsCurrent((await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, correction))!,
                source.Reviewed.Partition, correction, DateTimeOffset.UtcNow));
            Assert.Null(preview.ApplyBlockedReason);
            var result = await erasure.EraseSourceAsync(fixture.OrganizationId, source.ViewingEmployee, correction, source.User,
                new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
            Assert.Equal("completed", result.Status);
        }
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, source.Original.Id));
    }

    [MemoryPostgresTheory]
    [InlineData("missing")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-reviewer")]
    [InlineData("wrong-reference")]
    public async Task OrganizationCorrectionCannotInventCleanupOwnershipFromAnInvalidReceipt(string receiptState)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedOrganizationCorrectionAsync(fixture, receiptState);
        await using var db = fixture.Context();
        var preview = await ErasureService(fixture, db).GetErasureImpactAsync(fixture.OrganizationId,
            source.ViewingEmployee, source.Reviewed.Id, source.User);
        Assert.Equal("memory_erasure_source_review_required", preview.ApplyBlockedReason);
        Assert.Null(preview.EvidenceToken);
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(source.Reviewed.Partition, source.Reviewed.Id));
        Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
    }
}
