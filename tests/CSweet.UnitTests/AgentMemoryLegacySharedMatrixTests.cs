using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Organization")] [InlineData("Team")] [InlineData("Role")]
    public async Task LegacySharedMatrixKeepsExactBrokerReadButDeniesNativeOperatorAdoptionAndAdministration(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind, legacyShared: true);
        var records = await SeedScopedRecordsAsync(fixture, episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        var read = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(episode.Partition, episode.Scope, "Alice", Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic })), default);
        Assert.True(read.Succeeded, read.Error); Assert.Contains("Alice", read.Payload.ToStringUtf8());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        var native = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(episode.Partition with { ApplicationId = "csweet" }, episode.Scope, "Alice")), default);
        Assert.True(native.Succeeded, native.Error); Assert.DoesNotContain("Alice", native.Payload.ToStringUtf8());
        var recovery = IngestionRecovery(fixture, db); var review = ErasureService(fixture, db);
        Assert.Empty((await recovery.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, records.Claim.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, records.Procedure.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, records.Block.Id, user));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => review.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, user));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToArrayAsync()); Assert.Empty(await db.MemoryReviewReceipts.ToArrayAsync());
        Assert.False((await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id))!.IsSuppressed);
    }

    [MemoryPostgresTheory]
    [InlineData("Team")] [InlineData("Role")]
    public async Task SharedDirectReviewMatrixDeniesClaimsProceduresAndCoreWithoutWideningMembershipRights(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind);
        var records = await SeedScopedRecordsAsync(fixture, episode);
        await using var db = fixture.Context(); var review = ErasureService(fixture, db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, records.Claim.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetProcedureAsync(fixture.OrganizationId, fixture.EmployeeId, records.Procedure.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, records.Block.Id, user));
        Assert.Equal(episode.Id, (await review.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, user)).RecordId);
        Assert.Empty(await db.MemoryReviewReceipts.ToArrayAsync());
    }
}
