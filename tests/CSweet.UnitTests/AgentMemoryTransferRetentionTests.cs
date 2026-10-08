using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid Actor, Guid Target, Guid Source, Guid Copy, Guid Package)> SeedHeldTransferAsync(DurabilityFixture fixture)
    {
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor, true);
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfer.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        var applied = await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "apply"));
        return (actor, target, claim.EpisodeId, applied.AppliedEpisodeId!.Value, draft.PackageId);
    }

    private static async Task<ReviewMemoryHoldRequest> DecideHoldAsync(AgentMemoryReviewService review, Guid organization,
        Guid employee, Guid episode, Guid actor, bool held)
    {
        var preview = await review.GetHoldAsync(organization, employee, episode, actor);
        var request = new ReviewMemoryHoldRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, held);
        await review.ReviewHoldAsync(organization, employee, episode, actor, request);
        return request;
    }

    private static async Task<MemoryEpisode> RetainedEpisodeAsync(DurabilityFixture fixture, Guid id)
    {
        await using var db = fixture.Context();
        var payload = await db.Database.SqlQuery<string>($"SELECT payload::text AS \"Value\" FROM csweet_memory_episodes WHERE id={id}").SingleAsync();
        return JsonSerializer.Deserialize<MemoryEpisode>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<Guid> ImportRetentionCopyAsync(DurabilityFixture fixture, KnowledgeTransferPackage package,
        MemoryEpisode copy, MemoryTransferEvidence evidence)
    {
        // Model a separately imported row, preserving the immutable fingerprints on existing records.
        var id = Guid.NewGuid();
        package = package with { Id = Guid.NewGuid(), AppliedEpisodeId = id };
        evidence = evidence with { PackageId = package.Id, PackageFingerprint = MemoryTransferEvidence.Fingerprint(package),
            Records = evidence.Records.Select(x => x.Id == copy.Id ? x with { Id = id } : x).ToArray() };
        package = package with { ApprovedEvidence = evidence };
        await ((IKnowledgeTransferStore)fixture.Store).WriteKnowledgeTransferAsync(package);
        await fixture.Store.AppendEpisodeAsync(copy with { Id = id, IdempotencyKey = "knowledge-transfer:" + package.Id.ToString("D"),
            Source = new("knowledge-transfer", package.Id.ToString("D"), package.SourceEmployeeId),
            Content = MemoryTransferEvidence.RenderContent(package), TransferEvidence = evidence });
        return id;
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldRequiresExplicitUpstreamAndCopyReviewsAndPreservesOriginalCertificate()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var before = await RetainedEpisodeAsync(fixture, setup.Copy);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        Assert.True(preview.IsTransferred); Assert.True(preview.LegalHold); Assert.False(preview.CanRelease);
        Assert.Equal(1, preview.UpstreamSources); Assert.Equal(1, preview.HeldUpstreamSources);
        Assert.Equal("memory_transfer_upstream_held", preview.ReleaseBlocker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false)));
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        Assert.True((await RetainedEpisodeAsync(fixture, setup.Copy)).LegalHold);
        preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        Assert.True(preview.CanRelease); Assert.Equal(0, preview.HeldUpstreamSources);
        var request = await DecideHoldAsync(review, fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, false);
        var after = await RetainedEpisodeAsync(fixture, setup.Copy);
        Assert.False(after.LegalHold); Assert.Equal(before.SourceFingerprint, after.SourceFingerprint);
        Assert.Equal(JsonSerializer.Serialize(before.TransferEvidence), JsonSerializer.Serialize(after.TransferEvidence));
        Assert.True(MemorySourceIntegrity.IsVerified(after));
        // A later source hold must not be undone by replaying the already-committed decision.
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, true);
        var replay = await review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, request);
        Assert.True(replay.WasReplay); Assert.True((await RetainedEpisodeAsync(fixture, setup.Source)).LegalHold);
        Assert.False((await RetainedEpisodeAsync(fixture, setup.Copy)).LegalHold);
        Assert.Empty(await fixture.Store.SearchAsync(new(TransferTarget(fixture, setup.Target), MemoryScope.User, "concise")));
        // Raw library deletion remains conservative; the certificate is not silently rewritten.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.DeleteScopeAsync(TransferTarget(fixture, setup.Target)));
        Assert.Equal(1, await db.MemoryReviewReceipts.CountAsync(x => x.MemoryId == setup.Copy));
        Assert.Equal(1, await db.AuditOutbox.CountAsync(x => x.SourceEntityId == setup.Copy));
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldReviewBindsChangedUpstreamInventoryEvenWhenCopyRevisionIsUnchanged()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, true);
        var current = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        Assert.Equal(preview.Revision, current.Revision); Assert.NotEqual(preview.EvidenceToken, current.EvidenceToken);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false)));
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        // Restoring the old source values cannot restore an earlier review's revision evidence.
        Assert.True((await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor)).CanRelease);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false)));
        Assert.True((await RetainedEpisodeAsync(fixture, setup.Copy)).LegalHold);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferredHoldRequiresCurrentOriginAuthorityOnPreviewApplyAndReplay(bool replay)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        var request = new ReviewMemoryHoldRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false);
        if (replay) await review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, request);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(y => y.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, request));
        Assert.Equal(!replay, (await RetainedEpisodeAsync(fixture, setup.Copy)).LegalHold);
    }

    [MemoryPostgresTheory]
    [InlineData("missing-package")]
    [InlineData("missing-source")]
    [InlineData("altered-copy")]
    [InlineData("altered-source")]
    [InlineData("quarantined-package")]
    [InlineData("oversized-certificate")]
    [InlineData("cyclic-certificate")]
    [InlineData("oversized-retained-payload")]
    [InlineData("unsupported-audience")]
    public async Task TransferredHoldCannotReleaseUnverifiableRetainedEvidence(string fault)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var copyId = setup.Copy;
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        if (fault == "missing-package") await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_transfers WHERE id={setup.Package}");
        else if (fault == "missing-source") await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={setup.Source}");
        else if (fault == "quarantined-package") await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO csweet_memory_partition_migration_rows(table_name,record_id,previous_key,destination_key,disposition,reason,plan_fingerprint,payload_hash)
            VALUES ('csweet_memory_transfers',{setup.Package.ToString("D")},'unreviewed','unreviewed','Quarantine','fixture','fixture','fixture')
            """);
        else if (fault == "oversized-retained-payload")
        {
            // Nested JSON string escaping must count toward the full fingerprint bound.
            // Unknown legacy fields remain retained even though they are not evidence content.
            var extension = new string('\\', 5_000_000);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{retentionReviewFixture}}',to_jsonb({extension})) WHERE id={setup.Source}");
        }
        else if (fault is "oversized-certificate" or "cyclic-certificate" or "unsupported-audience")
        {
            var copy = await RetainedEpisodeAsync(fixture, setup.Copy);
            var package = (await ((IKnowledgeTransferStore)fixture.Store).GetKnowledgeTransferAsync(setup.Package))!;
            var source = copy.TransferEvidence!.Records.First(x => x.Kind == MemoryRecordKind.Episode);
            if (fault == "unsupported-audience") package = package with
                { SourceNamespaces = [package.SourceNamespaces[0] with { Audience = MemoryAudienceType.Role }] };
            var evidence = copy.TransferEvidence with { PackageFingerprint = MemoryTransferEvidence.Fingerprint(package), Records = fault == "oversized-certificate"
                ? Enumerable.Repeat(source, MemoryTransferEvidence.MaximumRecords + 1).ToArray()
                : fault == "cyclic-certificate" ? [source with { Id = copy.Id }] : copy.TransferEvidence.Records };
            copyId = await ImportRetentionCopyAsync(fixture, package, copy, evidence);
        }
        else
        {
            var changed = fault == "altered-copy" ? setup.Copy : setup.Source;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"altered\"') WHERE id={changed}");
        }
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, copyId, setup.Actor);
        Assert.False(preview.CanRelease); Assert.Equal("memory_transfer_retention_review_required", preview.ReleaseBlocker);
        Assert.Equal("Transferred evidence is unavailable until its retained source lineage can be verified.", preview.Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, copyId, setup.Actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false)));
        Assert.True((await RetainedEpisodeAsync(fixture, copyId)).LegalHold);
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldCannotUseTargetAuthorityToReviewAnotherHumansPrivateUpstreamAudience()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var package = await ((IKnowledgeTransferStore)fixture.Store).GetKnowledgeTransferAsync(setup.Package);
        Assert.NotNull(package);
        // Model an imported certificate whose origin audience is private to someone else.
        // Integrity alone must never establish this reviewer's authority over that audience.
        var foreign = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"),
            fixture.EmployeeId.ToString("D"), Guid.NewGuid().ToString("D"), "csweet");
        package = package with { SourceNamespaces = [foreign], Items = package.Items.Select(x => x with { SourcePartition = foreign.Partition }).ToArray() };
        var evidence = package.ApprovedEvidence! with { PackageFingerprint = MemoryTransferEvidence.Fingerprint(package),
            Records = package.ApprovedEvidence!.Records.Select(x => x with { Partition = foreign.Partition }).ToArray() };
        var copy = await RetainedEpisodeAsync(fixture, setup.Copy);
        var copyId = await ImportRetentionCopyAsync(fixture, package, copy, evidence);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, setup.Target, copyId, setup.Actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, copyId, setup.Actor,
            new(Guid.NewGuid(), 1, new string('a', 64), false)));
        Assert.True((await RetainedEpisodeAsync(fixture, copyId)).LegalHold);
    }

    [MemoryPostgresTheory]
    [InlineData("expired")]
    [InlineData("suppressed")]
    [InlineData("revoked")]
    public async Task TransferredHoldRetentionReadsSuppressedAndExpiredUpstreamSourcesWithoutRestoringRecall(string state)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        if (state == "expired") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{expiresAt}}',to_jsonb({DateTimeOffset.UtcNow.AddDays(-1)})) WHERE id={setup.Source}");
        else if (state == "suppressed") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{isSuppressed}}','true') WHERE id={setup.Source}");
        else
        {
            var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
            var package = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, setup.Package, setup.Actor);
            await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, setup.Package, setup.Actor, new(Guid.NewGuid(), package.ReviewToken, "reject"));
        }
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        Assert.True(preview.CanRelease); Assert.Equal(1, preview.UpstreamSources);
        await DecideHoldAsync(review, fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, false);
        Assert.Empty(await fixture.Store.SearchAsync(new(TransferTarget(fixture, setup.Target), MemoryScope.User, "concise")));
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldReleaseRollsBackWithAuditAndRetriesSameOperation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        var request = new ReviewMemoryHoldRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_transfer_hold_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_hold_audit_failure'; END $$;
            CREATE TRIGGER fail_transfer_hold_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryEpisode') EXECUTE FUNCTION fail_transfer_hold_review();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, request));
        Assert.True((await RetainedEpisodeAsync(fixture, setup.Copy)).LegalHold);
        Assert.Equal(preview.Revision, (await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor)).Revision);
        Assert.False(await db.MemoryReviewReceipts.AnyAsync(x => x.OperationId == request.OperationId));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_transfer_hold_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_transfer_hold_review();");
        var result = await review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, request);
        Assert.False(result.LegalHold);
        Assert.True((await review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, request)).WasReplay);
        var history = await review.ReadHistoryAsync(fixture.OrganizationId, setup.Target, "Episode", setup.Copy, setup.Actor);
        Assert.Equal("release-hold", Assert.Single(history.Items[^1].Reviews).Action);
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldReviewTracesNestedCopiesAndDoesNotCascadeTheirRelease()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context();
        var original = await db.AgentInstallations.SingleAsync(x => x.Id == fixture.InstallationId);
        var installation = new CSweet.Domain.Setup.AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
            BusinessId = fixture.OrganizationId.ToString("D"), PackageVersionId = original.PackageVersionId, IsEnabled = true };
        var target = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Agent,
            AgentInstallation = installation, AgentInstallationId = installation.Id, ReportsToOrganizationUserId = fixture.HumanId };
        db.CoreOrganizationUsers.Add(target);
        db.CoreConversations.Add(new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            AgentOrganizationUserId = target.Id, InitiatedByOrganizationUserId = fixture.HumanId });
        await db.SaveChangesAsync();
        var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var prepared = await transfers.PrepareAsync(fixture.OrganizationId, setup.Target, setup.Actor,
            new(Guid.NewGuid(), target.Id, "Relationship", [new("Episode", setup.Copy)], "Second reviewed handoff."));
        var transfer = await transfers.GetAsync(fixture.OrganizationId, setup.Target, prepared.PackageId, setup.Actor);
        await transfers.TransitionAsync(fixture.OrganizationId, setup.Target, prepared.PackageId, setup.Actor, new(Guid.NewGuid(), transfer.ReviewToken, "approve"));
        transfer = await transfers.GetAsync(fixture.OrganizationId, setup.Target, prepared.PackageId, setup.Actor);
        var applied = await transfers.TransitionAsync(fixture.OrganizationId, setup.Target, prepared.PackageId, setup.Actor, new(Guid.NewGuid(), transfer.ReviewToken, "apply"));
        var outer = applied.AppliedEpisodeId!.Value;
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, target.Id, outer, setup.Actor);
        Assert.Equal(2, preview.UpstreamSources); Assert.Equal(2, preview.HeldUpstreamSources); Assert.False(preview.CanRelease);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        preview = await review.GetHoldAsync(fixture.OrganizationId, target.Id, outer, setup.Actor);
        Assert.Equal(1, preview.HeldUpstreamSources); Assert.False(preview.CanRelease);
        await DecideHoldAsync(review, fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor, false);
        Assert.True((await RetainedEpisodeAsync(fixture, outer)).LegalHold);
        preview = await review.GetHoldAsync(fixture.OrganizationId, target.Id, outer, setup.Actor);
        Assert.Equal(2, preview.UpstreamSources); Assert.True(preview.CanRelease);
        await DecideHoldAsync(review, fixture.OrganizationId, target.Id, outer, setup.Actor, false);
        Assert.False((await RetainedEpisodeAsync(fixture, outer)).LegalHold);
        Assert.Empty(await fixture.Store.SearchAsync(new(TransferTarget(fixture, target.Id), MemoryScope.User, "concise")));
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldConcurrentDecisionsProduceOneReceiptAndConflictingReplayIsRejected()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using var db = fixture.Context(); var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await DecideHoldAsync(review, fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        var request = new ReviewMemoryHoldRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false);
        async Task<bool> Decide()
        {
            await using var context = fixture.Context();
            return (await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewHoldAsync(fixture.OrganizationId,
                setup.Target, setup.Copy, setup.Actor, request)).WasReplay;
        }
        var results = await Task.WhenAll(Decide(), Decide());
        Assert.Single(results, x => x); Assert.Single(results, x => !x);
        Assert.Equal(1, await db.MemoryReviewReceipts.CountAsync(x => x.MemoryId == setup.Copy));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewHoldAsync(fixture.OrganizationId, setup.Target,
            setup.Copy, setup.Actor, request with { LegalHold = true }));
    }

    [MemoryPostgresFact]
    public async Task TransferredHoldReviewKeepsUpstreamHoldAndOriginAuthorityLockedThroughCommit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await SeedHeldTransferAsync(fixture);
        await using (var initial = fixture.Context()) await DecideHoldAsync(new(initial, fixture.Store, TimeProvider.System),
            fixture.OrganizationId, fixture.EmployeeId, setup.Source, setup.Actor, false);
        var gate = new ReviewCommitGate(); await using var db = fixture.Context(gate);
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor);
        var pending = review.ReviewHoldAsync(fixture.OrganizationId, setup.Target, setup.Copy, setup.Actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var concurrent = fixture.Context(); await concurrent.Database.OpenConnectionAsync();
            await concurrent.Database.ExecuteSqlRawAsync("SET lock_timeout='200ms'");
            var source = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={setup.Source}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, source.SqlState);
            var authority = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"CoreOrganizationUsers\" SET \"ReportsToOrganizationUserId\"=NULL WHERE \"Id\"={fixture.EmployeeId}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, authority.SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False((await RetainedEpisodeAsync(fixture, setup.Copy)).LegalHold);
    }
}
