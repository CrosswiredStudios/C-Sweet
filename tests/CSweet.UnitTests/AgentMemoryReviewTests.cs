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
    public async Task ReviewHoldsCurrentAuthorityAndSourceUntilAuditAndMutationCommit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        var gate = new ReviewCommitGate();
        await using var db = fixture.Context(gate);
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        var pending = service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var concurrent = fixture.Context();
            await concurrent.Database.OpenConnectionAsync();
            await concurrent.Database.ExecuteSqlRawAsync("SET lock_timeout='200ms'");
            var sourceError = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{sensitivity}}','4') WHERE id={claim.EpisodeId}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, sourceError.SqlState);
            var actorError = await Assert.ThrowsAsync<InvalidOperationException>(() => concurrent.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(actorError.InnerException).SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        await pending;
        await using var revoke = fixture.Context();
        await revoke.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request));
    }

    private sealed class ReviewCommitGate : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CSweet.Domain.Core.MemoryReviewReceipt>().Any(x => x.State == EntityState.Added))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private static async Task<MemoryClaim> SeedReviewClaim(DurabilityFixture fixture)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = new MemoryEpisode(Guid.NewGuid(), fixture.Partition, MemoryScope.User, "Alice prefers concise replies",
            "text/plain", new("user", "fixture"), "checksum", now, now, Sensitivity: MemorySensitivity.Personal);
        var entity = new MemoryEntity(Guid.NewGuid(), fixture.Partition, "person", "Alice", [], null, false, now, now)
            { Sensitivity = MemorySensitivity.Personal, SourceEpisodeIds = [source.Id] };
        var claim = new MemoryClaim(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "prefers", null, "concise replies",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Internal, 1, 1, now, null, now);
        await fixture.Store.AppendEpisodeAsync(source); await fixture.Store.UpsertEntityAsync(entity); await fixture.Store.WriteClaimAsync(claim);
        return claim;
    }

    [MemoryPostgresFact]
    public async Task ClaimReviewConfirmsWithAuthenticatedReceiptAndReplayDoesNotUndoLaterReview()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context();
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        Assert.True(preview.CanConfirm); Assert.Equal("Personal", preview.Sensitivity);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        var result = await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request);
        Assert.False(result.WasReplay); Assert.True(result.ResultRevision > preview.Revision);
        Assert.Equal(MemoryConfirmationState.Confirmed, (await fixture.Store.GetClaimAsync(claim.Id))!.Confirmation);
        var receipt = await db.MemoryReviewReceipts.SingleAsync();
        Assert.Equal(actor, receipt.ActorApplicationUserId); Assert.Equal(fixture.HumanId, receipt.ActorOrganizationUserId);
        Assert.Equal(1, await db.AuditOutbox.CountAsync(x => x.Id == receipt.Id));
        Assert.DoesNotContain("concise", receipt.RequestHash);
        var next = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor,
            new(Guid.NewGuid(), next.Revision, next.EvidenceToken, "reject"));
        var replay = await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request);
        Assert.True(replay.WasReplay); Assert.Equal(result.ReceiptId, replay.ReceiptId);
        Assert.Equal(MemoryConfirmationState.Rejected, (await fixture.Store.GetClaimAsync(claim.Id))!.Confirmation);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewClaimAsync(fixture.OrganizationId,
            fixture.EmployeeId, claim.Id, actor, request with { Action = "reject" }));
        Assert.Equal(2, await db.MemoryReviewReceipts.CountAsync());
        receipt.Action = "rewritten";
        db.Update(receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [MemoryPostgresFact]
    public async Task ClaimCorrectionPreservesSourcesSensitivityAndAtomicHistory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "detailed replies");
        var result = await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request);
        var replacement = (await fixture.Store.GetClaimAsync(result.ResultClaimId))!;
        Assert.Equal(claim.Id, replacement.SupersedesClaimId); Assert.Contains(claim.EpisodeId, replacement.SourceEpisodeIds);
        Assert.Equal(MemorySensitivity.Personal, replacement.Sensitivity); Assert.Equal(MemoryTrustTier.ConfirmedUser, replacement.Trust);
        Assert.Equal(MemoryConfirmationState.Confirmed, replacement.Confirmation); Assert.Equal("detailed replies", replacement.Value);
        Assert.Equal(replacement.ValidFrom, (await fixture.Store.GetClaimAsync(claim.Id))!.ValidTo);
        var source = (await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, replacement.EpisodeId))!;
        Assert.Equal(fixture.HumanId.ToString("D"), source.Source.Author); Assert.True(MemorySourceIntegrity.IsVerified(source));
        Assert.Equal(MemorySensitivity.Personal, source.Sensitivity);
        Assert.True((await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request)).WasReplay);
        Assert.Equal(2, (await fixture.Store.ExportAsync(fixture.Partition)).Claims.Count);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ClaimReviewRechecksEvidenceAndCurrentAuthorityBeforeMutating()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{sensitivity}}','4') WHERE id={claim.EpisodeId}");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request));
        preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        Assert.Equal("Restricted", preview.Sensitivity);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"changed\"') WHERE id={claim.EpisodeId}");
        var invalid = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        Assert.False(invalid.CanConfirm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor,
            request with { ExpectedRevision = invalid.Revision, EvidenceToken = invalid.EvidenceToken }));
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(MemoryConfirmationState.Pending, (await fixture.Store.GetClaimAsync(claim.Id))!.Confirmation);
    }

    [MemoryPostgresFact]
    public async Task ConcurrentReviewsHaveOneWinnerAndFailedAuditRollsBackAllCorrectionEffects()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "new value");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_memory_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_review_failure'; END $$;
            CREATE TRIGGER fail_memory_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryClaim') EXECUTE FUNCTION fail_memory_review();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request));
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Single(raw.Claims); Assert.Single(raw.Episodes); Assert.Null(raw.Claims[0].ValidTo);
        Assert.Single((await ((IMemoryRevisionReader)fixture.Store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Claim, claim.Id)).Items);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_memory_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_memory_review();");
        async Task<bool> Attempt(string action)
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewClaimAsync(fixture.OrganizationId,
                fixture.EmployeeId, claim.Id, actor, request with { OperationId = Guid.NewGuid(), Action = action, ReplacementValue = null }); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        var winners = await Task.WhenAll(Attempt("confirm"), Attempt("reject"));
        Assert.Single(winners, x => x); Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ReviewMigrationIsAdditiveAndCannotAuthorizeOtherEmployeeNamespaces()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context();
        var migration = new AuthenticatedMemoryReview(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(new ProcedureMemoryReview().UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.NotNull(await fixture.Store.GetClaimAsync(claim.Id)); Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        var foreign = claim with { Id = Guid.NewGuid(), Partition = claim.Partition with { AgentId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.WriteClaimAsync(foreign);
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, foreign.Id, actor));
    }
}
