using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Case", "package-change")] [InlineData("Conversation", "package-change")]
    [InlineData("Case", "reader-restore")] [InlineData("Conversation", "reader-restore")]
    [InlineData("Case", "suppression")] [InlineData("Conversation", "suppression")]
    [InlineData("Case", "forged-content")] [InlineData("Conversation", "forged-content")]
    [InlineData("Case", "foreign-source")] [InlineData("Conversation", "foreign-source")]
    [InlineData("Case", "foreign-target")] [InlineData("Conversation", "foreign-target")]
    [InlineData("Case", "unlisted-source")] [InlineData("Conversation", "unlisted-source")]
    public async Task ScopedAudienceStoredPackageQueryBindsCurrentSourcesTargetAndPackageState(string kind, string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var records = await SeedScopedRecordsAsync(fixture, scoped.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await BindScopedChatWorkAsync(fixture, work);
        var ns = new MemoryNamespace(scoped.Episode.Partition, scoped.Episode.Scope,
            kind == "Case" ? MemoryAudienceType.Case : MemoryAudienceType.Conversation,
            (scoped.Item ?? scoped.Conversation).ToString("D"));
        var projection = MemoryReadProjection.Create(await fixture.Store.ExportAsync(ns.Partition), ns.Partition,
            MemorySensitivity.Personal, DateTimeOffset.UtcNow);
        var item = MemoryReadProjection.TransferItems(projection, ns.Partition).Single(x => x.MemoryId == records.Claim.Id);
        // Existing stored-package inspection is distinct from platform transfer preparation/application.
        var package = new KnowledgeTransferPackage(Guid.NewGuid(), session.BusinessId, ns.AudienceId,
            ns.AudienceId, [ns], ns, "Alice stored package debrief", [item], MemorySensitivity.Personal,
            KnowledgeTransferStatus.PendingApproval, DateTimeOffset.UtcNow, fixture.HumanId.ToString("D"));
        var foreign = ns with { Partition = ns.Partition with { TenantId = Guid.NewGuid().ToString("D") } };
        package = change switch
        {
            "forged-content" => package with { Items = [item with { Content = "Alice invented evidence" }] },
            "foreign-source" => package with { SourceNamespaces = [foreign] },
            "foreign-target" => package with { TargetNamespace = foreign },
            "unlisted-source" => package with { Items = [item with { SourcePartition = foreign.Partition }] },
            _ => package
        };
        await ((IKnowledgeTransferStore)fixture.Store).WriteKnowledgeTransferAsync(package);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        var request = ReadRequest("get-knowledge-transfer", new { packageId = package.Id });
        var read = await handler.HandleAsync(session, request, default);
        if (change is "forged-content" or "foreign-source" or "foreign-target" or "unlisted-source")
        {
            Assert.DoesNotContain("Alice", read.Payload.ToStringUtf8());
            if (change.StartsWith("foreign-", StringComparison.Ordinal))
            { Assert.False(read.Succeeded); Assert.Equal("memory_policy_denied", read.FailureCode); }
            else { Assert.True(read.Succeeded, read.Error); Assert.Equal("null", read.Payload.ToStringUtf8()); }
            Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
            return;
        }
        Assert.True(read.Succeeded, read.Error);
        var returned = JsonSerializer.Deserialize<KnowledgeTransferPackage>(read.Payload.Span, ReadEvidenceJson)!;
        Assert.Equal(package.Id, returned.Id);
        var returnedItem = Assert.Single(returned.Items);
        Assert.Equal(item.MemoryId, returnedItem.MemoryId); Assert.Equal(item.Content, returnedItem.Content);
        Assert.Equal(item.EpisodeIds, returnedItem.EpisodeIds);
        Assert.Equal(package.Debrief, returned.Debrief);
        var receipt = Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Contains("scopedAuthorityHash", receipt.EvidenceJson); Assert.DoesNotContain("Alice", receipt.EvidenceJson);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        if (change == "package-change")
            await ((IKnowledgeTransferStore)fixture.Store).WriteKnowledgeTransferAsync(package with { Status = KnowledgeTransferStatus.Rejected });
        else if (change == "reader-restore")
        {
            await SetScopedReaderRevokedAsync(fixture, kind, true);
            var denied = await handler.HandleAsync(session, request, default);
            Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode);
            Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
            await SetScopedReaderRevokedAsync(fixture, kind, false);
        }
        else
        {
            await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(ns.Partition, scoped.Episode.Id);
            var withheld = await handler.HandleAsync(session, request, default);
            Assert.True(withheld.Succeeded, withheld.Error); Assert.Equal("null", withheld.Payload.ToStringUtf8());
        }
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("Case", "semantic")] [InlineData("Conversation", "semantic")]
    [InlineData("Case", "procedure")] [InlineData("Conversation", "procedure")]
    [InlineData("Case", "core")] [InlineData("Conversation", "core")]
    [InlineData("Case", "find-entity")] [InlineData("Conversation", "find-entity")]
    [InlineData("Case", "find-key")] [InlineData("Conversation", "find-key")]
    [InlineData("Case", "get-claim")] [InlineData("Conversation", "get-claim")]
    [InlineData("Case", "list-claims")] [InlineData("Conversation", "list-claims")]
    [InlineData("Case", "export")] [InlineData("Conversation", "export")]
    public async Task ScopedAudienceDerivedRoutesRequireEverySourceIncludingGraphEndpointsAndEmbeddings(string kind, string route)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var secondaryId = Guid.NewGuid();
        var secondary = scoped.Episode with { Id = secondaryId, IdempotencyKey = secondaryId.ToString("D"),
            Source = scoped.Episode.Source with { Id = secondaryId.ToString("D") }, Content = "Second contributor" };
        await fixture.Store.AppendEpisodeAsync(secondary);
        var records = await SeedScopedRecordsAsync(fixture, scoped.Episode, [secondaryId]);
        records = records with { Embedding = records.Embedding with { Id = Guid.NewGuid(), MemoryId = records.Claim.Id, Layer = MemoryLayer.Semantic } };
        await fixture.Store.WriteEmbeddingAsync(records.Embedding);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindScopedChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        var request = ScopedRouteRequest(scoped.Episode, records, route);
        var positive = await handler.HandleAsync(session, request, default);
        Assert.True(positive.Succeeded, positive.Error);
        if (route == "export")
        {
            var before = JsonSerializer.Deserialize<MemoryExport>(positive.Payload.Span, ReadEvidenceJson)!;
            Assert.Contains(before.Claims, x => x.Id == records.Claim.Id);
            Assert.Contains(before.Edges, x => x.Id == records.Edge.Id);
            Assert.Contains(before.Embeddings!, x => x.Id == records.Embedding.Id);
        }
        else AssertScopedRouteRecords(positive, scoped.Episode, records, route);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(scoped.Episode.Partition, secondaryId);
        var withheld = await handler.HandleAsync(session, request, default);
        Assert.True(withheld.Succeeded, withheld.Error);
        if (route == "export")
        {
            var after = JsonSerializer.Deserialize<MemoryExport>(withheld.Payload.Span, ReadEvidenceJson)!;
            Assert.Contains(after.Episodes, x => x.Id == scoped.Episode.Id);
            Assert.DoesNotContain(after.Entities, x => x.Id == records.Entity.Id);
            Assert.Empty(after.Claims); Assert.Empty(after.Edges); Assert.Empty(after.Blocks); Assert.Empty(after.Procedures);
            Assert.DoesNotContain(after.Embeddings!, x => x.Id == records.Embedding.Id);
            Assert.Contains(after.Embeddings!, x => x.MemoryId == scoped.Episode.Id && x.Layer == MemoryLayer.Episodic);
        }
        else if (route is "get-claim" or "find-entity" or "find-key") Assert.Equal("null", withheld.Payload.ToStringUtf8());
        else Assert.Equal("[]", withheld.Payload.ToStringUtf8());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
    }
}
