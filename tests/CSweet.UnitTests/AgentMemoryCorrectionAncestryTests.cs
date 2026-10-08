using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task RetainedSharedCertificateCannotAuthorizeARewrittenApprovedSource(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var applied = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [new("Episode", source.Id)], "Alice reviewed handoff", SourceAudienceId: audience));
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        Assert.True((await review.GetHoldAsync(fixture.OrganizationId, target, applied.AppliedEpisodeId!.Value, user)).CanRelease);
        // A damaged/imported store must fail retained verification even if an altered
        // source was independently resealed. Normal writers cannot remove this guard.
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER csweet_memory_source_fingerprint_guard ON csweet_memory_episodes");
        var rewritten = MemorySourceIntegrity.Seal(source with { Content = "Alice different approved evidence" });
        var payload = System.Text.Json.JsonSerializer.Serialize(rewritten, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=CAST({payload} AS jsonb) WHERE id={source.Id}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.GetHoldAsync(fixture.OrganizationId, target, applied.AppliedEpisodeId.Value, user));
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.GetSuppressionAsync(fixture.OrganizationId, target, applied.AppliedEpisodeId.Value, user));
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task SharedCorrectionAncestryRequiresHumanReceiptAndCurrentMembershipForRetentionAndDispatch(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, kind, audience); var third = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var applied = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [], "Alice handoff", SourceAudienceId: audience));
        var partition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), target.ToString("D"), "csweet").Partition;
        var store = (PostgreSqlMemoryStore)fixture.Store; var operation = Guid.NewGuid(); var originalClaim = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var ancestry = await store.CaptureCorrectionEvidenceAsync(partition, operation, [applied.AppliedEpisodeId!.Value]);
        var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Agent, "Alice corrected preference", "text/plain",
            new("user", operation.ToString("D"), fixture.HumanId.ToString("D")), "checksum", now, now, Sensitivity: MemorySensitivity.Personal,
            OperationalReferences: [new("memory-claim", originalClaim.ToString("D"), "1")]) { CorrectionEvidence = ancestry };
        await store.AppendEpisodeAsync(episode); episode = (await store.GetEpisodeAsync(partition, episode.Id))!;
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.GetHoldAsync(fixture.OrganizationId, target, episode.Id, user));
        // Seed trusted review evidence to exercise read/retention integration before
        // enabling the shared correction write route in the human-review workflow.
        var receipt = new MemoryReviewReceipt { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeId = target,
            RecordKind = "Claim", MemoryId = originalClaim, ResultMemoryId = Guid.NewGuid(), OperationId = operation,
            ActorApplicationUserId = user, ActorOrganizationUserId = fixture.HumanId, RequestHash = "fixture-correction",
            Action = "confirm", PreviousRevision = 1, ResultRevision = 1, CreatedAt = now };
        db.MemoryReviewReceipts.Add(receipt); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.GetHoldAsync(fixture.OrganizationId, target, episode.Id, user));
        await db.MemoryReviewReceipts.Where(x => x.Id == receipt.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Action, "correct"));
        var hold = await review.GetHoldAsync(fixture.OrganizationId, target, episode.Id, user); Assert.True(hold.IsTransferred); Assert.True(hold.CanRelease);
        Assert.NotEmpty((await review.ReadHistoryAsync(fixture.OrganizationId, target, "Episode", episode.Id, user)).Items);
        Assert.NotNull(await fixture.Service(db, new UsageProviderFactory()).GetItemAsync(fixture.OrganizationId, target, episode.Id, applicationUserId: user));
        var read = await new MemoryRecallDispatchEvidence(db).CaptureReadAsync(episode, partition, default);
        Assert.Contains(source.Partition, read!.Partitions);
        var nestedRequest = new PrepareMemoryTransferRequest(Guid.NewGuid(), third, "Employee", [new("Episode", episode.Id)], "Alice corrected handoff");
        var nested = await ApplySharedAsync(transfers, fixture, target, user, nestedRequest);
        var nestedPartition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), third.ToString("D"), "csweet").Partition;
        var nestedEpisode = (await store.GetEpisodeAsync(nestedPartition, nested.AppliedEpisodeId!.Value))!;
        Assert.Equal(source.Partition, Assert.Single(nestedEpisode.TransferEvidence!.RequiredSharedPartitions!));
        Assert.Contains(nestedEpisode.TransferEvidence.Records, x => x.Id == episode.Id);
        Assert.Contains(nestedEpisode.TransferEvidence.Records, x => x.Id == applied.AppliedEpisodeId);
        var nestedHold = await review.GetHoldAsync(fixture.OrganizationId, third, nestedEpisode.Id, user); Assert.True(nestedHold.CanRelease);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, target, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, third, nestedEpisode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReadHistoryAsync(fixture.OrganizationId, target, "Episode", episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfers.PrepareAsync(fixture.OrganizationId, target, user, nestedRequest));
        Assert.Null(await fixture.Service(db, new UsageProviderFactory()).GetItemAsync(fixture.OrganizationId, target, episode.Id, applicationUserId: user));
        Assert.Null(await fixture.Service(db, new UsageProviderFactory()).GetItemAsync(fixture.OrganizationId, third, nestedEpisode.Id, applicationUserId: user));
    }
}
