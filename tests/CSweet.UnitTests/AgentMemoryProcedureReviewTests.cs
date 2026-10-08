using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task ProcedureReviewRejectsForeignAudienceAndDatabaseRejectsMismatchedPartition()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var foreign = procedure with { Id = Guid.NewGuid(), Partition = procedure.Partition with { UserId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.WriteProcedureAsync(foreign);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, foreign.Id, actor));
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE csweet_memory_procedures SET partition_key={foreign.Partition.StorageKey} WHERE id={procedure.Id}"));
        Assert.Contains("memory_canonical_partition_required", mismatch.Message);
        Assert.True((await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor)).CanConfirm);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    private static async Task<ProceduralMemory> SeedReviewProcedure(DurabilityFixture fixture, bool held = false)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        var source = new MemoryEpisode(Guid.NewGuid(), fixture.Partition, MemoryScope.User, "primary evidence", "text/plain",
            new("user", "primary"), "checksum", now, now, ExpiresAt: now.AddDays(3), Sensitivity: MemorySensitivity.Personal);
        var extra = source with { Id = Guid.NewGuid(), Content = "additional evidence", Source = new("user", "extra"),
            Sensitivity = MemorySensitivity.Confidential, ExpiresAt = now.AddDays(1), LegalHold = held };
        await fixture.Store.AppendEpisodeAsync(source); await fixture.Store.AppendEpisodeAsync(extra);
        var procedure = new ProceduralMemory(Guid.NewGuid(), fixture.Partition, source.Id, "Deployment checklist", "Verify deployment approval before release.",
            "Production releases only", 3, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, now, now.AddDays(2), now)
            { SourceEpisodeIds = [extra.Id] };
        await fixture.Store.WriteProcedureAsync(procedure); return procedure;
    }

    [MemoryPostgresFact]
    public async Task ProcedureReviewConfirmsWithoutElevatingTrustAndReplayCannotUndoRejection()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        Assert.True(preview.CanConfirm); Assert.Equal("Confidential", preview.Sensitivity); Assert.Equal(2, preview.SourceEpisodeIds.Count);
        var request = new ReviewMemoryProcedureRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        var result = await service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request);
        var stored = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Procedures);
        Assert.Equal(MemoryConfirmationState.Confirmed, stored.Confirmation); Assert.Equal(MemoryTrustTier.AgentInference, stored.Trust);
        Assert.Equal(1, await db.MemoryReviewReceipts.CountAsync(x => x.RecordKind == "Procedure"));
        Assert.Equal(1, await db.AuditOutbox.CountAsync(x => x.Id == result.ReceiptId));
        var downgrade = db.GetService<IMigrationsSqlGenerator>().Generate(new ProcedureMemoryReview().DownOperations);
        var downgradeError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(downgrade[0].CommandText));
        Assert.Contains("memory_review_downgrade_requires_snapshot", downgradeError.Message);
        Assert.Equal("Procedure", (await db.MemoryReviewReceipts.AsNoTracking().SingleAsync()).RecordKind);
        Assert.Contains(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "deployment")), x => x.Id == procedure.Id && x.Sensitivity == MemorySensitivity.Confidential);
        var next = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        await service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor,
            new(Guid.NewGuid(), next.Revision, next.EvidenceToken, "reject"));
        var replay = await service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request);
        Assert.True(replay.WasReplay); Assert.Equal(result.ReceiptId, replay.ReceiptId);
        Assert.Equal(MemoryConfirmationState.Rejected, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Procedures).Confirmation);
        Assert.DoesNotContain(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "deployment")), x => x.Id == procedure.Id);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor,
            request with { Action = "reject" }));
    }

    [MemoryPostgresFact]
    public async Task ProcedureCorrectionCreatesVersionAndRetainsApplicabilitySourcesExpiryAndHold()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture, held: true);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        var request = new ReviewMemoryProcedureRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct",
            new("Deployment safety", "Check deployment approval and rollback readiness.", "Production after QA approval"));
        var result = await service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request);
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        var replacement = Assert.Single(raw.Procedures, x => x.Id == result.ResultProcedureId);
        Assert.Equal(4, replacement.Version); Assert.Equal(request.Correction!.Applicability, replacement.Applicability);
        Assert.Equal(MemoryTrustTier.ConfirmedUser, replacement.Trust); Assert.Equal(MemoryConfirmationState.Confirmed, replacement.Confirmation);
        Assert.True(preview.SourceEpisodeIds.ToHashSet().SetEquals(replacement.SourceEpisodeIds));
        Assert.Equal(procedure.ValidTo, replacement.ValidTo);
        Assert.Equal(replacement.ValidFrom, Assert.Single(raw.Procedures, x => x.Id == procedure.Id).ValidTo);
        var source = Assert.Single(raw.Episodes, x => x.Id == replacement.EpisodeId);
        Assert.True(source.LegalHold); Assert.Equal(MemorySensitivity.Confidential, source.Sensitivity);
        Assert.Equal(raw.Episodes.Where(x => preview.SourceEpisodeIds.Contains(x.Id)).Min(x => x.ExpiresAt), source.ExpiresAt);
        Assert.Equal(fixture.HumanId.ToString("D"), source.Source.Author);
        Assert.Equal(procedure.Id.ToString("D"), Assert.Single(source.OperationalReferences!).Id);
        Assert.True(MemorySourceIntegrity.IsVerified(source));
        Assert.True((await service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request)).WasReplay);
        Assert.Equal(2, (await fixture.Store.ExportAsync(fixture.Partition)).Procedures.Count);
    }

    [MemoryPostgresFact]
    public async Task ProcedureReviewRejectsChangedEvidenceAndCurrentAuthorityRevocation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        var request = new ReviewMemoryProcedureRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"altered\"') WHERE id={procedure.SourceEpisodeIds[0]}");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request));
        var invalid = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        Assert.False(invalid.CanConfirm); Assert.False(invalid.CanCorrect); Assert.True(invalid.CanReject);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor,
            request with { EvidenceToken = invalid.EvidenceToken }));
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ProcedureAuditFailureRollsBackCorrectionAndConcurrentReviewsHaveOneWinner()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        var request = new ReviewMemoryProcedureRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", new("New", "New procedure", null));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_procedure_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_procedure_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryProcedure') EXECUTE FUNCTION fail_procedure_review();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request));
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Single(raw.Procedures); Assert.Equal(procedure.ValidTo, raw.Procedures[0].ValidTo); Assert.Equal(2, raw.Episodes.Count);
        Assert.Single((await ((IMemoryRevisionReader)fixture.Store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Procedure, procedure.Id)).Items);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_procedure_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_procedure_review();");
        async Task<bool> Attempt(string text)
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewProcedureAsync(fixture.OrganizationId,
                fixture.EmployeeId, procedure.Id, actor, request with { OperationId = Guid.NewGuid(), Correction = new("New", text, null) }); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt("First"), Attempt("Second")), x => x);
        Assert.Equal(2, (await fixture.Store.ExportAsync(fixture.Partition)).Procedures.Count);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ProcedureReceiptUpgradePreservesHistoricalClaimReceiptAndReplay()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        var result = await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request);
        var migration = new ProcedureMemoryReview(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        db.ChangeTracker.Clear();
        var receipt = await db.MemoryReviewReceipts.SingleAsync();
        Assert.Equal("Claim", receipt.RecordKind); Assert.Equal(claim.Id, receipt.MemoryId); Assert.Equal(result.ReceiptId, receipt.Id);
        Assert.True((await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request)).WasReplay);
    }

    [MemoryPostgresFact]
    public async Task TransferredClaimAndProcedureReviewsValidateOriginalEvidenceAndRejectRevokedApproval()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, original) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, original));
        var preview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        var approved = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        var applied = await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), approved.ReviewToken, "apply"));
        var partition = TransferTarget(fixture, target); var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now)
            { SourceEpisodeIds = [applied.AppliedEpisodeId!.Value], Sensitivity = MemorySensitivity.Personal };
        await fixture.Store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), partition, applied.AppliedEpisodeId.Value, entity.Id, "prefers", null, "concise",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now);
        var procedure = new ProceduralMemory(Guid.NewGuid(), partition, applied.AppliedEpisodeId.Value, "Replies", "Keep replies concise", null, 1,
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, now, null, now);
        await fixture.Store.WriteClaimAsync(claim); await fixture.Store.WriteProcedureAsync(procedure);
        var reviews = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var claimPreview = await reviews.GetClaimAsync(fixture.OrganizationId, target, claim.Id, actor);
        var procedurePreview = await reviews.GetProcedureAsync(fixture.OrganizationId, target, procedure.Id, actor);
        Assert.True(claimPreview.CanConfirm); Assert.True(procedurePreview.CanConfirm);
        await reviews.ReviewClaimAsync(fixture.OrganizationId, target, claim.Id, actor,
            new(Guid.NewGuid(), claimPreview.Revision, claimPreview.EvidenceToken, "confirm"));
        var oldRequest = new ReviewMemoryProcedureRequest(Guid.NewGuid(), procedurePreview.Revision, procedurePreview.EvidenceToken, "confirm");
        await fixture.Store.SetClaimConfirmationAsync(original.Id, MemoryConfirmationState.Rejected);
        Assert.False((await reviews.GetClaimAsync(fixture.OrganizationId, target, claim.Id, actor)).CanConfirm);
        Assert.False((await reviews.GetProcedureAsync(fixture.OrganizationId, target, procedure.Id, actor)).CanConfirm);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => reviews.ReviewProcedureAsync(fixture.OrganizationId, target, procedure.Id, actor, oldRequest));
    }

    [MemoryPostgresFact]
    public async Task ProcedureReviewHoldsSourceAndCurrentActorUntilAuditCommit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture);
        var gate = new ReviewCommitGate();
        await using var db = fixture.Context(gate); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        var request = new ReviewMemoryProcedureRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        var pending = service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var concurrent = fixture.Context(); await concurrent.Database.OpenConnectionAsync();
            await concurrent.Database.ExecuteSqlRawAsync("SET lock_timeout='200ms'");
            var sourceError = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={procedure.EpisodeId}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, sourceError.SqlState);
            var actorError = await Assert.ThrowsAsync<InvalidOperationException>(() => concurrent.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(actorError.InnerException).SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        await pending;
        await using var revoke = fixture.Context();
        await revoke.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request));
    }

    [MemoryPostgresFact]
    public async Task OrganizationProcedureCorrectionRetainsTenantScopeAndRequiresTopLevelHuman()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var partition = EmployeeMemoryNamespaces.Organization(fixture.OrganizationId.ToString("D"), "csweet").Partition;
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Tenant, "organization source", "text/plain",
            new("application", "policy"), "checksum", now, now, Sensitivity: MemorySensitivity.Internal);
        await fixture.Store.AppendEpisodeAsync(source);
        var procedure = new ProceduralMemory(Guid.NewGuid(), partition, source.Id, "Organization procedure", "Original", null, 1,
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, now, null, now);
        await fixture.Store.WriteProcedureAsync(procedure);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor);
        var request = new ReviewMemoryProcedureRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", new("Organization procedure", "Corrected", null));
        var result = await service.ReviewProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, procedure.Id, actor, request);
        var raw = await fixture.Store.ExportAsync(partition);
        Assert.Equal(MemoryScope.Tenant, Assert.Single(raw.Episodes, x => x.Id == raw.Procedures.Single(p => p.Id == result.ResultProcedureId).EpisodeId).Scope);
        var ancestor = new CSweet.Domain.Core.OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            EmployeeType = CSweet.Domain.Core.EmployeeType.Human };
        db.CoreOrganizationUsers.Add(ancestor); await db.SaveChangesAsync();
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(y => y.ReportsToOrganizationUserId, ancestor.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, result.ResultProcedureId, actor));
    }
}
