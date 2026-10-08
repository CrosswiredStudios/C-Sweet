using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
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
    private static async Task<(Guid Actor, Guid Target, MemoryClaim Claim)> SeedTransferAsync(DurabilityFixture fixture)
    {
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var claim = await SeedReviewClaim(fixture);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        await using var db = fixture.Context();
        var original = await db.AgentInstallations.SingleAsync(x => x.Id == fixture.InstallationId);
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), BusinessId = fixture.OrganizationId.ToString("D"),
            PackageVersionId = original.PackageVersionId, IsEnabled = true };
        var target = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Agent,
            AgentInstallation = installation, AgentInstallationId = installation.Id, ReportsToOrganizationUserId = fixture.HumanId };
        db.CoreOrganizationUsers.Add(target);
        db.CoreConversations.Add(new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            AgentOrganizationUserId = target.Id, InitiatedByOrganizationUserId = fixture.HumanId });
        await db.SaveChangesAsync();
        return (actor, target.Id, claim);
    }
    private static PrepareMemoryTransferRequest TransferRequest(Guid target, MemoryClaim claim) =>
        new(Guid.NewGuid(), target, "Relationship", [new("Claim", claim.Id)], "A reviewed preference handoff.");
    private static MemoryPartition TransferTarget(DurabilityFixture fixture, Guid target) =>
        EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"), target.ToString("D"), fixture.HumanId.ToString("D"), "csweet").Partition;

    [MemoryPostgresFact]
    public async Task AuthenticatedTransferCommitsAuditAndReplayWithoutResurrectingRevokedCopy()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var prepare = TransferRequest(target, claim);
        var draft = await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, prepare);
        var repeated = await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, prepare);
        Assert.True(repeated.WasReplay); Assert.Equal(draft.PackageId, repeated.PackageId);
        Assert.Contains((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, actor)).Items, x => x.PackageId == draft.PackageId);
        var preview = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        Assert.True(preview.CanApprove); Assert.Contains("concise replies", preview.Content);
        await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        var approved = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        Assert.True(approved.CanApply);
        var apply = new TransitionMemoryTransferRequest(Guid.NewGuid(), approved.ReviewToken, "apply");
        var result = await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, apply);
        Assert.Equal("Applied", result.Status); Assert.NotNull(result.AppliedEpisodeId);
        var partition = TransferTarget(fixture, target);
        Assert.Single(await fixture.Store.SearchAsync(new(partition, MemoryScope.User, "concise")));
        var current = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor,
            new(Guid.NewGuid(), current.ReviewToken, "reject"));
        var replay = await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, apply);
        Assert.True(replay.WasReplay); Assert.Equal(result.ReceiptId, replay.ReceiptId);
        Assert.Empty(await fixture.Store.SearchAsync(new(partition, MemoryScope.User, "concise")));
        Assert.Equal("Rejected", (await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor)).Status);
        Assert.Equal(4, await db.MemoryTransferReceipts.CountAsync());
        Assert.Equal(4, await db.AuditOutbox.CountAsync(x => x.SourceEntityType == "MemoryTransfer"));
        Assert.Single(await db.AgentMemoryNamespaces.Where(x => x.EmployeeId == target && x.UserId == fixture.HumanId).ToListAsync());
        Assert.Single((await fixture.Store.ExportAsync(partition)).Episodes);
        var receipt = await db.MemoryTransferReceipts.FirstAsync(); receipt.Action = "rewrite";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [MemoryPostgresFact]
    public async Task TransferRechecksSourceRevisionAndBothCurrentEmployeeAuthorities()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Rejected);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId,
            draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve")));
        Assert.False((await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor)).CanApprove);
        await db.CoreOrganizationUsers.Where(x => x.Id == target).ExecuteUpdateAsync(x => x.SetProperty(y => y.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor));
        Assert.Single(await db.MemoryTransferReceipts.ToListAsync());
        Assert.Empty((await fixture.Store.ExportAsync(TransferTarget(fixture, target))).Episodes);
    }

    [MemoryPostgresFact]
    public async Task FailedTransferAuditRollsBackEpisodePackageAndHistoryAndSameOperationCanRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        var approved = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        var request = new TransitionMemoryTransferRequest(Guid.NewGuid(), approved.ReviewToken, "apply");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_memory_transfer() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_transfer_failure'; END $$;
            CREATE TRIGGER fail_memory_transfer BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryTransfer') EXECUTE FUNCTION fail_memory_transfer();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request));
        var partition = TransferTarget(fixture, target);
        Assert.Empty((await fixture.Store.ExportAsync(partition)).Episodes);
        Assert.Equal("Approved", (await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor)).Status);
        Assert.Equal(2, await db.MemoryTransferReceipts.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_memory_transfer ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_memory_transfer();");
        var applied = await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request);
        Assert.False(applied.WasReplay);
        Assert.Single((await ((IMemoryRevisionReader)fixture.Store).ReadRevisionsAsync(partition, MemoryRecordKind.Episode, applied.AppliedEpisodeId!.Value)).Items);
        Assert.True((await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request)).WasReplay);
    }

    [MemoryPostgresFact]
    public async Task ConcurrentTransferApplicationsHaveOneWinnerAndConflictingOperationCannotReplay()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var prepare = TransferRequest(target, claim);
        var draft = await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, prepare);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId,
            actor, prepare with { Debrief = "changed" }));
        var preview = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        var approved = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        async Task<bool> Apply()
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryTransferService(context, fixture.Store, TimeProvider.System).TransitionAsync(fixture.OrganizationId,
                fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), approved.ReviewToken, "apply")); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Apply(), Apply()), x => x);
        Assert.Single((await fixture.Store.ExportAsync(TransferTarget(fixture, target))).Episodes);
        Assert.Equal(3, await db.MemoryTransferReceipts.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task TransferSelectionCannotWidenAnotherUsersRelationshipOrReadForeignEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var request = TransferRequest(target, claim);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor,
            request with { SourceScope = "Employee" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor,
            request with { Items = Enumerable.Repeat(new MemoryTransferSelection("Claim", claim.Id), 33).ToArray() }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync(Guid.NewGuid(), fixture.EmployeeId, actor, request));
        Assert.Empty(await db.MemoryTransferReceipts.ToListAsync());
        var migration = new AuthenticatedMemoryTransfers(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.NotNull(await fixture.Store.GetClaimAsync(claim.Id));
        Assert.Equal("PendingApproval", (await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, request)).Status);
    }

    [MemoryPostgresFact]
    public async Task TransferHoldsSourceAndTargetAuthorityUntilAtomicApplicationCompletes()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        var gate = new TransferCommitGate();
        await using var db = fixture.Context(gate); var service = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        var approved = await service.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        var request = new TransitionMemoryTransferRequest(Guid.NewGuid(), approved.ReviewToken, "apply");
        var pending = service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var concurrent = fixture.Context(); await concurrent.Database.OpenConnectionAsync();
            await concurrent.Database.ExecuteSqlRawAsync("SET lock_timeout='200ms'");
            var sourceError = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={claim.EpisodeId}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, sourceError.SqlState);
            var targetError = await Assert.ThrowsAsync<InvalidOperationException>(() => concurrent.CoreOrganizationUsers.Where(x => x.Id == target)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(targetError.InnerException).SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        await pending;
        await using var revoke = fixture.Context();
        await revoke.CoreOrganizationUsers.Where(x => x.Id == target).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request));
    }

    private sealed class TransferCommitGate : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken token = default)
        {
            if (data.Context!.ChangeTracker.Entries<MemoryTransferReceipt>().Any(x => x.State == EntityState.Added && x.Entity.Action == "apply"))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return result;
        }
    }

    [MemoryPostgresFact]
    public async Task TransferDiscoveryPaginatesStableTiesAndRejectsForeignCursors()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, _) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        var service = new AgentMemoryTransferService(db, fixture.Store, new TransferClock(DateTimeOffset.UtcNow.AddMinutes(-1)));
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 21; i++)
            expected.Add((await service.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor,
                new(Guid.NewGuid(), target, "Employee", [], $"Reviewed notes {i}"))).PackageId);
        var first = await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, actor);
        Assert.Equal(20, first.Items.Count); Assert.NotNull(first.NextCursor);
        var second = await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, actor, first.NextCursor);
        Assert.Single(second.Items); Assert.Null(second.NextCursor);
        Assert.True(expected.SetEquals(first.Items.Concat(second.Items).Select(x => x.PackageId)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, actor, Guid.NewGuid()));
    }
    private sealed class TransferClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}
