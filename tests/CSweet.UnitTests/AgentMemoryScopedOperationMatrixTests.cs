using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record ScopedRecordFixture(MemoryEntity Entity, MemoryClaim Claim, MemoryEdge Edge,
        MemoryBlock Block, ProceduralMemory Procedure, MemoryEmbedding Embedding);

    private static async Task<ScopedRecordFixture> SeedScopedRecordsAsync(DurabilityFixture fixture, MemoryEpisode episode,
        IReadOnlyList<Guid>? additionalSources = null)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var contributors = new[] { episode.Id }.Concat(additionalSources ?? []).Distinct().ToArray();
        var entity = new MemoryEntity(Guid.NewGuid(), episode.Partition, "person", "Alice", [], "person:alice", false, now, now)
            { SourceEpisodeIds = contributors, Sensitivity = episode.Sensitivity };
        var other = entity with { Id = Guid.NewGuid(), CanonicalName = "Bob", ApplicationKey = "person:bob" };
        await fixture.Store.UpsertEntityAsync(entity); await fixture.Store.UpsertEntityAsync(other);
        var claim = new MemoryClaim(Guid.NewGuid(), episode.Partition, episode.Id, entity.Id, "prefers", null, "concise replies for Alice",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, episode.Sensitivity, 1, 1, now, null, now)
            { SourceEpisodeIds = additionalSources ?? [] };
        var edge = new MemoryEdge(Guid.NewGuid(), episode.Partition, episode.Id, entity.Id, "knows", other.Id,
            MemoryTrustTier.AgentInference, 1, now, null, false, now);
        var block = new MemoryBlock(Guid.NewGuid(), episode.Partition, "Preferences", "Alice prefers concise replies", 1, 100, true,
            MemoryTrustTier.AgentInference, now) { SourceEpisodeIds = contributors, Sensitivity = episode.Sensitivity, Confirmation = MemoryConfirmationState.Confirmed };
        var procedure = new ProceduralMemory(Guid.NewGuid(), episode.Partition, episode.Id, "Alice reply", "Keep Alice's answer concise",
            "When answering Alice", 1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, now, null, now)
            { SourceEpisodeIds = additionalSources ?? [] };
        var embedding = new MemoryEmbedding(Guid.NewGuid(), episode.Partition, episode.Id, MemoryLayer.Episodic, [1f, 0f], "fixture", now);
        await fixture.Store.WriteClaimAsync(claim); await fixture.Store.WriteEdgeAsync(edge); await fixture.Store.WriteBlockAsync(block);
        await fixture.Store.WriteProcedureAsync(procedure); await fixture.Store.WriteEmbeddingAsync(embedding);
        return new(entity, claim, edge, block, procedure, embedding);
    }

    private static readonly string[] ScopedReadRoutes = ["episode", "semantic", "procedure", "core", "find-entity", "find-key", "get-claim", "list-claims", "export"];

    private static RequestCapability ScopedRouteRequest(MemoryEpisode episode, ScopedRecordFixture records, string route) => route switch
    {
        "find-entity" => ReadRequest("find-entity", new { partition = episode.Partition, canonicalName = "Alice" }),
        "find-key" => ReadRequest("find-entity-by-application-key", new { partition = episode.Partition, applicationKey = "person:alice" }),
        "get-claim" => ReadRequest("get-claim", new { claimId = records.Claim.Id }),
        "list-claims" => ReadRequest("list-claims", episode.Partition),
        "export" => ReadRequest("export", episode.Partition, CSweetMemoryCapabilities.Export),
        _ => ReadRequest("search", new MemorySearchRequest(episode.Partition, episode.Scope, "Alice", Layers: new HashSet<MemoryLayer>
            { route switch { "episode" => MemoryLayer.Episodic, "semantic" => MemoryLayer.Semantic, "procedure" => MemoryLayer.Procedural, "core" => MemoryLayer.Core, _ => throw new ArgumentException(nameof(route)) } }))
    };

    private static async Task BindScopedChatWorkAsync(DurabilityFixture fixture, AgentWorkItem work)
    {
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context();
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceType, "chat-turn")
            .SetProperty(p => p.SourceId, turn.Id.ToString("D")));
    }

    private static async Task SetScopedReaderRevokedAsync(DurabilityFixture fixture, string kind, bool revoked)
    {
        await using var db = fixture.Context();
        if (kind == "Case") await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.InstallationId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null));
        else await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null));
    }

    private static void AssertScopedRouteRecords(CapabilityResult result, MemoryEpisode episode, ScopedRecordFixture records, string route)
    {
        Assert.True(result.Succeeded, result.Error); Assert.Contains("Alice", result.Payload.ToStringUtf8());
        if (route == "export")
        {
            var export = JsonSerializer.Deserialize<MemoryExport>(result.Payload.Span, ReadEvidenceJson)!;
            Assert.Equal(episode.Id, Assert.Single(export.Episodes).Id); Assert.Equal(records.Claim.Id, Assert.Single(export.Claims).Id);
            Assert.Equal(records.Edge.Id, Assert.Single(export.Edges).Id); Assert.Equal(records.Block.Id, Assert.Single(export.Blocks).Id);
            Assert.Equal(records.Procedure.Id, Assert.Single(export.Procedures).Id); Assert.Equal(records.Embedding.Id, Assert.Single(export.Embeddings!).Id);
            Assert.Equal(2, export.Entities.Count);
        }
        else if (route == "semantic")
        {
            var items = JsonSerializer.Deserialize<MemoryCandidate[]>(result.Payload.Span, ReadEvidenceJson)!;
            Assert.Contains(items, x => x.Id == records.Claim.Id && x.RetrievalChannel == "semantic");
            Assert.Contains(items, x => x.Id == records.Edge.Id && x.RetrievalChannel == "graph");
        }
        else if (route is "episode" or "procedure" or "core")
        {
            var expected = route == "episode" ? episode.Id : route == "procedure" ? records.Procedure.Id : records.Block.Id;
            Assert.Equal(expected, Assert.Single(JsonSerializer.Deserialize<MemoryCandidate[]>(result.Payload.Span, ReadEvidenceJson)!).Id);
        }
        else if (route is "find-entity" or "find-key") Assert.Equal(records.Entity.Id, JsonSerializer.Deserialize<MemoryEntity>(result.Payload.Span, ReadEvidenceJson)!.Id);
        else if (route == "get-claim") Assert.Equal(records.Claim.Id, JsonSerializer.Deserialize<MemoryClaim>(result.Payload.Span, ReadEvidenceJson)!.Id);
        else Assert.Equal(records.Claim.Id, Assert.Single(JsonSerializer.Deserialize<MemoryClaim[]>(result.Payload.Span, ReadEvidenceJson)!).Id);
    }

    [MemoryPostgresTheory]
    [InlineData("Case", "episode")] [InlineData("Conversation", "episode")]
    [InlineData("Case", "semantic")] [InlineData("Conversation", "semantic")]
    [InlineData("Case", "procedure")] [InlineData("Conversation", "procedure")]
    [InlineData("Case", "core")] [InlineData("Conversation", "core")]
    [InlineData("Case", "find-entity")] [InlineData("Conversation", "find-entity")]
    [InlineData("Case", "find-key")] [InlineData("Conversation", "find-key")]
    [InlineData("Case", "get-claim")] [InlineData("Conversation", "get-claim")]
    [InlineData("Case", "list-claims")] [InlineData("Conversation", "list-claims")]
    [InlineData("Case", "export")] [InlineData("Conversation", "export")]
    public async Task ScopedAudienceOperationMatrixReadsAllExposedLayersAndRejectsRestoredReaderAuthority(string kind, string route)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, scoped.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindScopedChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        var request = ScopedRouteRequest(scoped.Episode, records, route);
        AssertScopedRouteRecords(await handler.HandleAsync(session, request, default), scoped.Episode, records, route);
        var receipt = Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Contains("scopedAuthorityHash", receipt.EvidenceJson); Assert.DoesNotContain("Alice", receipt.EvidenceJson);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await SetScopedReaderRevokedAsync(fixture, kind, true);
        var denied = await handler.HandleAsync(session, request, default);
        Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode); Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await SetScopedReaderRevokedAsync(fixture, kind, false);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("Case", "episode")] [InlineData("Conversation", "episode")]
    [InlineData("Case", "semantic")] [InlineData("Conversation", "semantic")]
    [InlineData("Case", "procedure")] [InlineData("Conversation", "procedure")]
    [InlineData("Case", "core")] [InlineData("Conversation", "core")]
    [InlineData("Case", "find-entity")] [InlineData("Conversation", "find-entity")]
    [InlineData("Case", "find-key")] [InlineData("Conversation", "find-key")]
    [InlineData("Case", "get-claim")] [InlineData("Conversation", "get-claim")]
    [InlineData("Case", "list-claims")] [InlineData("Conversation", "list-claims")]
    [InlineData("Case", "export")] [InlineData("Conversation", "export")]
    public async Task ScopedAudienceOperationMatrixSuppressionStopsRawGraphDerivedAndExportContent(string kind, string route)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, scoped.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindScopedChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        var request = ScopedRouteRequest(scoped.Episode, records, route);
        AssertScopedRouteRecords(await handler.HandleAsync(session, request, default), scoped.Episode, records, route);
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(scoped.Episode.Partition, scoped.Episode.Id);
        var withheld = await handler.HandleAsync(session, request, default);
        Assert.DoesNotContain("Alice", withheld.Payload.ToStringUtf8());
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
    }

    [MemoryPostgresTheory]
    [InlineData("Case", false)] [InlineData("Conversation", false)]
    [InlineData("Case", true)] [InlineData("Conversation", true)]
    public async Task ScopedAudienceOperationMatrixRejectsForeignAndMalformedPartitionsIncludingClaimIds(string kind, bool malformed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var bad = scoped.Episode with { Id = Guid.NewGuid(), IdempotencyKey = null, Partition = malformed
            ? scoped.Episode.Partition with { AgentId = fixture.EmployeeId.ToString("D") }
            : scoped.Episode.Partition with { TenantId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.AppendEpisodeAsync(bad); var records = await SeedScopedRecordsAsync(fixture, bad);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindScopedChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        foreach (var route in ScopedReadRoutes)
        {
            var denied = await handler.HandleAsync(session, ScopedRouteRequest(bad, records, route), default);
            Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode); Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        }
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Case")] [InlineData("Conversation")]
    public async Task ScopedAudienceOperationMatrixProposalsPreserveScopeAndCannotPromoteTrustedRecords(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, scoped.Episode);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture);
        Assert.IsType<HashSet<string>>(session.Grant.RequestedCapabilities).Add(CSweetMemoryCapabilities.Manage);
        await using var db = fixture.Context();
        var handler = new PlatformMemoryCapabilityHandler(fixture.Store, NullLogger<PlatformMemoryCapabilityHandler>.Instance,
            new AgentMemoryIdentityResolver(db), new PlatformMemoryReadEvidence(db), fixture.Service(db, new UsageProviderFactory()));
        var proposed = scoped.Episode with { Id = Guid.NewGuid(), Source = new("user", "forged"), LegalHold = true, IdempotencyKey = null, Sensitivity = MemorySensitivity.Public };
        var episode = await handler.HandleAsync(session, ReadRequest("append-episode", proposed, CSweetMemoryCapabilities.Write), default);
        Assert.True(episode.Succeeded, episode.Error);
        var proposedId = JsonSerializer.Deserialize<MemoryWriteResult>(episode.Payload.Span, ReadEvidenceJson)!.Id;
        var saved = (await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(scoped.Episode.Partition, proposedId))!;
        Assert.Equal(scoped.Episode.Scope, saved.Scope); Assert.Equal(scoped.Episode.Partition, saved.Partition);
        Assert.Equal("agent-proposal", saved.Source.Type); Assert.Equal(fixture.EmployeeId.ToString("D"), saved.Source.Author);
        Assert.False(saved.LegalHold); Assert.Equal(MemorySensitivity.Personal, saved.Sensitivity);
        var job = Assert.Single(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().ToArrayAsync());
        Assert.Equal(proposedId, job.EpisodeId); Assert.Equal(fixture.EmployeeId, job.EmployeeId);
        Assert.Equal(fixture.InstallationId, job.InstallationId);
        var claimResult = await handler.HandleAsync(session, ReadRequest("write-claim", records.Claim with { Id = Guid.NewGuid(), Trust = MemoryTrustTier.Authoritative }, CSweetMemoryCapabilities.Write), default);
        var procedureResult = await handler.HandleAsync(session, ReadRequest("write-procedure", records.Procedure with { Id = Guid.NewGuid(), Trust = MemoryTrustTier.Authoritative }, CSweetMemoryCapabilities.Write), default);
        Assert.True(claimResult.Succeeded, claimResult.Error); Assert.True(procedureResult.Succeeded, procedureResult.Error);
        var claimId = JsonSerializer.Deserialize<MemoryWriteResult>(claimResult.Payload.Span, ReadEvidenceJson)!.Id;
        var procedureId = JsonSerializer.Deserialize<MemoryWriteResult>(procedureResult.Payload.Span, ReadEvidenceJson)!.Id;
        var export = await fixture.Store.ExportAsync(scoped.Episode.Partition);
        var claim = Assert.Single(export.Claims, x => x.Id == claimId); var procedure = Assert.Single(export.Procedures, x => x.Id == procedureId);
        Assert.Equal(MemoryConfirmationState.Pending, claim.Confirmation); Assert.Equal(MemoryTrustTier.AgentInference, claim.Trust);
        Assert.Equal(MemoryConfirmationState.Pending, procedure.Confirmation); Assert.Equal(MemoryTrustTier.AgentInference, procedure.Trust);
        var embedding = records.Embedding with { Id = Guid.NewGuid() };
        Assert.True((await handler.HandleAsync(session, ReadRequest("write-embedding", embedding, CSweetMemoryCapabilities.Write), default)).Succeeded);
        var use = new MemoryUse(Guid.NewGuid(), scoped.Episode.Partition, "forged-invocation", scoped.Episode.Id, MemoryLayer.Episodic, MemoryUseOutcome.Supplied, DateTimeOffset.UtcNow);
        Assert.True((await handler.HandleAsync(session, ReadRequest("record-use", use, CSweetMemoryCapabilities.Write), default)).Succeeded);
        foreach (var request in new[] { ReadRequest("upsert-entity", records.Entity, CSweetMemoryCapabilities.Write),
            ReadRequest("write-block", records.Block, CSweetMemoryCapabilities.Write), ReadRequest("write-edge", records.Edge, CSweetMemoryCapabilities.Write),
            ReadRequest("set-confirmation", new { claimId = records.Claim.Id }, CSweetMemoryCapabilities.Manage),
            ReadRequest("supersede-claim", new { claimId = records.Claim.Id }, CSweetMemoryCapabilities.Manage),
            ReadRequest("write-knowledge-transfer", new { }, CSweetMemoryCapabilities.Manage), ReadRequest("delete-scope", scoped.Episode.Partition, CSweetMemoryCapabilities.Manage) })
        {
            var denied = await handler.HandleAsync(session, request, default); Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode);
        }
        var original = (await fixture.Store.GetClaimAsync(records.Claim.Id))!;
        Assert.Equal(MemoryConfirmationState.Confirmed, original.Confirmation); Assert.Equal(records.Claim.Value, original.Value);
        Assert.Equal(records.Claim.Trust, original.Trust);
        Assert.Empty(await db.MemoryReviewReceipts.ToArrayAsync());
        await SetScopedReaderRevokedAsync(fixture, kind, true);
        foreach (var request in new[] { ReadRequest("append-episode", proposed with { Id = Guid.NewGuid() }, CSweetMemoryCapabilities.Write),
            ReadRequest("write-claim", records.Claim with { Id = Guid.NewGuid() }, CSweetMemoryCapabilities.Write),
            ReadRequest("write-procedure", records.Procedure with { Id = Guid.NewGuid() }, CSweetMemoryCapabilities.Write),
            ReadRequest("write-embedding", embedding with { Id = Guid.NewGuid() }, CSweetMemoryCapabilities.Write),
            ReadRequest("record-use", use with { Id = Guid.NewGuid() }, CSweetMemoryCapabilities.Write) })
            Assert.Equal("memory_policy_denied", (await handler.HandleAsync(session, request, default)).FailureCode);
        var after = await fixture.Store.ExportAsync(scoped.Episode.Partition);
        Assert.Equal(export.Episodes.Count, after.Episodes.Count); Assert.Equal(export.Claims.Count, after.Claims.Count);
        Assert.Equal(export.Procedures.Count, after.Procedures.Count); Assert.Equal(2, after.Embeddings!.Count);
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().ToArrayAsync());
    }
}
