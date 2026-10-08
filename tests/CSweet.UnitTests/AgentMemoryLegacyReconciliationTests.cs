using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CSweet.AI.Providers;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid User, MemoryEpisode Episode, Guid Claim)> SeedPartialLegacyAsync(DurabilityFixture fixture,
        MemoryConfirmationState confirmation = MemoryConfirmationState.Confirmed)
    {
        var (user, episode) = await SeedJoblessProposalAsync(fixture); var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.Context()) await db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards);
        var alice = new MemoryEntity(Guid.NewGuid(), episode.Partition, "learned:Person", "Alice", [], null, false, now, now)
            { Sensitivity = MemorySensitivity.Confidential, SourceEpisodeIds = [episode.Id] };
        var bob = alice with { Id = Guid.NewGuid(), CanonicalName = "Bob", Sensitivity = MemorySensitivity.Personal };
        await fixture.Store.UpsertEntityAsync(alice); await fixture.Store.UpsertEntityAsync(bob);
        var claim = new MemoryClaim(Guid.NewGuid(), episode.Partition, episode.Id, alice.Id, "name", null, "Reviewed Alice",
            MemoryTrustTier.ConfirmedUser, confirmation, MemorySensitivity.Confidential, 1, 1, episode.OccurredAt, null, now)
            { SourceEpisodeIds = [episode.Id] };
        await fixture.Store.WriteClaimAsync(claim);
        await fixture.Store.WriteEdgeAsync(new(Guid.NewGuid(), episode.Partition, episode.Id, alice.Id, "learned:knows", bob.Id,
            MemoryTrustTier.ConfirmedUser, 1, episode.OccurredAt, null, true, now) { SourceEpisodeIds = [episode.Id] });
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(), episode.Partition, episode.Id, "Onboard", "Reviewed procedure", null,
            1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, episode.OccurredAt, null, now) { SourceEpisodeIds = [episode.Id] });
        await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), episode.Partition, "Identity", "Reviewed core text", 1, 100, false,
            MemoryTrustTier.ConfirmedUser, now) { Confirmation = MemoryConfirmationState.Confirmed, Sensitivity = MemorySensitivity.Confidential,
                SourceEpisodeIds = [episode.Id] });
        await fixture.Store.WriteEmbeddingAsync(new(Guid.NewGuid(), episode.Partition, episode.Id, MemoryLayer.Episodic, [1f,0f], "legacy-model", now));
        return (user, episode, claim.Id);
    }

    private sealed class ReconciliationProvider(Func<CancellationToken, Task>? before = null) : ILlmProviderFactory
    {
        public int Calls;
        public Task<IChatClient> CreateChatClientAsync(Guid id, CancellationToken token = default) => CreateChatClientAsync(id, "test", token);
        public Task<IChatClient> CreateChatClientAsync(Guid id, string? model, CancellationToken token = default) =>
            Task.FromResult<IChatClient>(new ReconciliationClient(async ct => { Calls++; if (before is not null) await before(ct); }));
    }
    private sealed class ReconciliationClient(Func<CancellationToken, Task> before) : DelegatingChatClient(new UsageChatClient())
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await before(cancellationToken);
            return new(new ChatMessage(ChatRole.Assistant, """
              {"entities":[{"type":"Person","name":"Alice","aliases":["New alias"]},{"type":"Person","name":"Bob","aliases":[]},{"type":"Person","name":"Carol","aliases":[]}],
               "claims":[{"subjectName":"Alice","predicate":"NAME","value":"Model replacement","confidence":1,"importance":1,"sensitivity":"Public"},
                         {"subjectName":"Alice","predicate":"city","value":"London","confidence":1,"importance":1,"sensitivity":"Public"}],
               "edges":[{"fromName":"Alice","relationship":"knows","toName":"Bob","confidence":1},{"fromName":"Alice","relationship":"mentors","toName":"Carol","confidence":1}],
               "procedures":[{"name":"ONBOARD","procedure":"Model replacement"},{"name":"Publish","procedure":"Novel pending procedure"}]}
              """)) { Usage = new UsageDetails { InputTokenCount=10,OutputTokenCount=10,TotalTokenCount=20 } };
        }
    }

    [MemoryPostgresTheory]
    [InlineData(MemoryConfirmationState.Confirmed)]
    [InlineData(MemoryConfirmationState.Rejected)]
    [InlineData(MemoryConfirmationState.Pending)]
    public async Task LegacyReconciliationRequiresExplicitChoiceAndPreservesEveryRetainedRecord(MemoryConfirmationState state)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, claimId) = await SeedPartialLegacyAsync(fixture, state);
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        var original = await fixture.Store.ExportAsync(episode.Partition);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.True(preview.CanQueue); Assert.Equal(7, preview.ExistingRecords); Assert.Equal("preserve-existing-v1", preview.RequiredReconciliationPolicy);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "overwrite-existing")));
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, preview.RequiredReconciliationPolicy);
        await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        Assert.Contains("preserve-existing-v1", (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).SourceJson);
        var provider = new ReconciliationProvider(); Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        var result = await fixture.Store.ExportAsync(episode.Partition);
        Assert.Equal(JsonSerializer.Serialize(original.Claims.Single()), JsonSerializer.Serialize(result.Claims.Single(x => x.Id == claimId)));
        foreach (var entity in original.Entities) Assert.Equal(JsonSerializer.Serialize(entity), JsonSerializer.Serialize(result.Entities.Single(x => x.Id == entity.Id)));
        Assert.Equal(JsonSerializer.Serialize(original.Edges.Single()), JsonSerializer.Serialize(result.Edges.Single(x => x.Id == original.Edges.Single().Id)));
        Assert.Equal(JsonSerializer.Serialize(original.Procedures.Single()), JsonSerializer.Serialize(result.Procedures.Single(x => x.Id == original.Procedures.Single().Id)));
        Assert.Equal(JsonSerializer.Serialize(original.Blocks), JsonSerializer.Serialize(result.Blocks));
        Assert.Equal(JsonSerializer.Serialize(original.Embeddings), JsonSerializer.Serialize(result.Embeddings));
        Assert.Equal(2, result.Claims.Count); var city = Assert.Single(result.Claims, x => x.Predicate == "city");
        Assert.Equal(MemoryConfirmationState.Pending, city.Confirmation); Assert.Equal(MemorySensitivity.Confidential, city.Sensitivity); Assert.Null(city.SupersedesClaimId);
        Assert.Equal(2, result.Edges.Count); Assert.Equal(2, result.Procedures.Count);
        Assert.Equal(MemoryConfirmationState.Pending, Assert.Single(result.Procedures, x => x.Name == "Publish").Confirmation);
        Assert.True((await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request)).Replayed);
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        using var accepted = JsonDocument.Parse(job.AcceptedExtractionJson!); Assert.Equal(5, accepted.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Contains("preserve-existing-v1", job.AcceptedExtractionJson!);
    }

    [MemoryPostgresTheory]
    [InlineData("claim")]
    [InlineData("addition")]
    [InlineData("delete")]
    public async Task LegacyReconciliationBindsTheExactRecordInventoryAtQueue(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, claim) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await ChangeLegacyOutputAsync(fixture, db, episode, claim, change);
        var error = await Record.ExceptionAsync(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, preview.RequiredReconciliationPolicy)));
        Assert.True(error is DbUpdateConcurrencyException or InvalidOperationException, error?.ToString());
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
    }

    private static async Task ChangeLegacyOutputAsync(DurabilityFixture fixture, Infrastructure.Persistence.CSweetDbContext db,
        MemoryEpisode episode, Guid claim, string change)
    {
        if (change == "claim") await fixture.Store.SetClaimConfirmationAsync(claim, MemoryConfirmationState.Rejected);
        else if (change == "delete") await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_edges WHERE episode_id={episode.Id}");
        else await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(), episode.Partition, episode.Id, "Additional", "Human addition", null,
            1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, episode.OccurredAt, null, DateTimeOffset.UtcNow) { SourceEpisodeIds=[episode.Id] });
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReconciliationRechecksInventoryBeforeDispatchAndAcceptingOutput(bool duringExtraction)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, claim) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, preview.RequiredReconciliationPolicy));
        if (!duringExtraction) await ChangeLegacyOutputAsync(fixture, db, episode, claim, "claim");
        var provider = new ReconciliationProvider(duringExtraction ? async _ => {
            await using var modifying = fixture.Context(); await ChangeLegacyOutputAsync(fixture, modifying, episode, claim, "claim"); } : null);
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(duringExtraction ? 1 : 0, provider.Calls);
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status); Assert.Equal("memory_enrichment_source_invalidated", job.LastError); Assert.Null(job.AcceptedExtractionJson);
        Assert.Equal(MemoryConfirmationState.Rejected, (await fixture.Store.GetClaimAsync(claim))!.Confirmation);
        Assert.Single((await fixture.Store.ExportAsync(episode.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task LegacyReconciliationReplaysVersionedAcceptedOutputAfterAtomicApplicationFailure()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, preview.RequiredReconciliationPolicy));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_reconciliation_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected application failure'; END $$;
            CREATE TRIGGER fail_reconciliation_claim BEFORE INSERT ON csweet_memory_claims FOR EACH ROW EXECUTE FUNCTION fail_reconciliation_claim();
            """);
        var provider = new ReconciliationProvider(); Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(); Assert.NotNull(job.AcceptedExtractionJson);
        var partial = await fixture.Store.ExportAsync(episode.Partition); Assert.Equal(2, partial.Entities.Count); Assert.Single(partial.Claims);
        Assert.Single(partial.Procedures); Assert.Single(partial.Edges);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_reconciliation_claim ON csweet_memory_claims; DROP FUNCTION fail_reconciliation_claim();");
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await using var restarted = fixture.Context();
        var differentProvider = new ReconciliationProvider(_ => throw new InvalidOperationException("A new call is forbidden after acceptance"));
        Assert.Equal(1, await fixture.Service(restarted, differentProvider).ProcessPendingAsync()); Assert.Equal(0, differentProvider.Calls);
        Assert.Equal(job.AcceptedExtractionJson, (await restarted.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).AcceptedExtractionJson);
        Assert.Equal(2, (await fixture.Store.ExportAsync(episode.Partition)).Claims.Count);
    }

    [MemoryPostgresTheory]
    [InlineData("unclassified")]
    [InlineData("unlinked")]
    [InlineData("oversized")]
    public async Task LegacyReconciliationBlocksUnverifiableOrOversizedRetainedOutputs(string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context();
        var entity = (await fixture.Store.ExportAsync(episode.Partition)).Entities.Single(x => x.CanonicalName == "Alice").Id;
        if (defect == "unclassified") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_entities SET payload=payload-'sensitivity' WHERE id={entity}");
        else if (defect == "unlinked") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_entities SET payload=jsonb_set(payload,'{{sourceEpisodeIds}}','[]') WHERE id={entity}");
        else await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_entities SET payload=jsonb_set(payload,'{{legacyText}}',to_jsonb({new string('x',70000)}::text)) WHERE id={entity}");
        var preview = await IngestionRecovery(fixture, db).PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.False(preview.CanQueue); Assert.Equal("memory_ingestion_legacy_output_unavailable", preview.BlockedReason);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task LegacyReconciliationInventoryHasARecordCapacityBound()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO csweet_memory_embeddings(id,partition_key,memory_id,layer,payload)
            SELECT ids.id,e.partition_key,e.memory_id,e.layer,jsonb_set(e.payload,ARRAY['id'],to_jsonb(ids.id::text))
            FROM csweet_memory_embeddings e CROSS JOIN (SELECT gen_random_uuid() AS id FROM generate_series(1,257)) ids
            WHERE e.memory_id={episode.Id}
            """);
        var preview = await IngestionRecovery(fixture, db).PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.False(preview.CanQueue); Assert.Equal("memory_ingestion_legacy_output_unavailable", preview.BlockedReason);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task LegacyReconciliationPreservesOlderOrdinaryRequestAndSnapshotIdentity()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        var olderIdentity = JsonSerializer.SerializeToUtf8Bytes(new { organizationId=fixture.OrganizationId, employeeId=fixture.EmployeeId,
            episodeId=episode.Id, applicationUserId=user, actor=fixture.HumanId,
            request=new { request.OperationId,request.ExpectedRevision,request.EvidenceToken } });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(olderIdentity)).ToLowerInvariant(),(await db.MemoryReviewReceipts.SingleAsync()).RequestHash);
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(); Assert.DoesNotContain("Reconciliation",job.SourceJson);
        var provider = new ScriptedProviderFactory((_,_)=>Task.CompletedTask);
        Assert.Equal(1,await fixture.Service(db,provider).ProcessPendingAsync());
        Assert.DoesNotContain("Reconciliation",(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).AcceptedExtractionJson!);
        Assert.True((await service.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,request)).Replayed);
    }

    [MemoryPostgresFact]
    public async Task LegacyReconciliationEnforcesTheDurableSnapshotByteBoundInPreview()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, claim) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var longKey = new string('p',10000);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO csweet_memory_claims(id,partition_key,episode_id,subject_id,predicate,value,confirmation,valid_from,valid_to,payload)
            SELECT items.id,c.partition_key,c.episode_id,c.subject_id,{longKey}||items.n::text,c.value,c.confirmation,c.valid_from,c.valid_to,
              jsonb_set(jsonb_set(c.payload,ARRAY['id'],to_jsonb(items.id::text)),ARRAY['predicate'],to_jsonb({longKey}||items.n::text))
            FROM csweet_memory_claims c CROSS JOIN (SELECT gen_random_uuid() AS id,n FROM generate_series(1,150) n) items WHERE c.id={claim}
            """);
        var preview = await IngestionRecovery(fixture, db).PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.False(preview.CanQueue); Assert.Equal("memory_ingestion_source_unavailable",preview.BlockedReason);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReconciliationRetainedContributorBlocksIncompleteForgetting(bool completed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.AcceptedExtractionJson, (string?)null)
            .SetProperty(j => j.ExtractionAcceptedAt, (DateTimeOffset?)null).SetProperty(j => j.LastError, (string?)null));
        await SeedRecallTurnAsync(fixture);
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(),episode.Partition,fixture.MessageId,"Sensitive contributor name","Retained contributor procedure",null,
            1,MemoryTrustTier.ConfirmedUser,MemoryConfirmationState.Confirmed,episode.OccurredAt,null,DateTimeOffset.UtcNow)
            { SourceEpisodeIds=[episode.Id,fixture.MessageId] });
        var inventory = await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(episode.Partition,fixture.MessageId);
        Assert.DoesNotContain(inventory.Targets,x=>x.Kind==MemoryErasureKind.Episode && x.Id==episode.Id);
        var recovery = IngestionRecovery(fixture,db);
        var preview = await recovery.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        Assert.True(preview.CanQueue);
        await recovery.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken,preview.RequiredReconciliationPolicy));
        if (completed) Assert.Equal(1,await fixture.Service(db,new ReconciliationProvider()).ProcessPendingAsync());
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        using var source = JsonDocument.Parse(job.SourceJson);
        Assert.Contains(fixture.MessageId,source.RootElement.GetProperty("Reconciliation").GetProperty("SourceEpisodeIds").EnumerateArray().Select(x=>x.GetGuid()));
        var erase = await new AgentMemoryReviewService(db,fixture.Store,TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,fixture.MessageId,user);
        Assert.Null(erase.ApplyBlockedReason);
        Assert.Equal(2,erase.Execution!.ExtractionJobs);
        db.ChangeTracker.Clear();
        var result=await ErasureService(fixture,db).EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,fixture.MessageId,user,
            new(Guid.NewGuid(),Assert.IsType<string>(erase.EvidenceToken)));
        Assert.Equal("completed",result.Status);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition,episode.Id));
    }

    [MemoryPostgresTheory]
    [InlineData("older-worker")]
    [InlineData("changed-policy")]
    [InlineData("wrong-source")]
    [InlineData("missing-policy")]
    public async Task LegacyReconciliationDatabaseRejectsOutputThatIgnoresTheReviewedPolicy(string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await service.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken,preview.RequiredReconciliationPolicy));
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j=>j.Status,MemoryCaptureStatus.Processing)
            .SetProperty(j=>j.LeaseToken,Guid.NewGuid()).SetProperty(j=>j.LeaseExpiresAt,DateTimeOffset.UtcNow.AddMinutes(5)).SetProperty(j=>j.Attempts,1));
        var source = JsonNode.Parse(job.SourceJson)!;
        var output = new JsonObject { ["SchemaVersion"]=5,["Episode"]=source["Episode"]!.DeepClone(),
            ["Reconciliation"]=source["Reconciliation"]!.DeepClone(),["GenericSourceHash"]=job.SourceHash,
            ["Enrichment"]=new JsonObject(),["Provider"]=new JsonObject(),["ExtractorVersion"]="test" };
        if (defect=="older-worker") output["SchemaVersion"]=4;
        if (defect=="changed-policy") output["Reconciliation"]!["Policy"]="overwrite";
        if (defect=="wrong-source") output["GenericSourceHash"]=new string('a',64);
        if (defect=="missing-policy") output.Remove("Reconciliation");
        var body=output.ToJsonString();
        var error=await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MemoryEpisodeEnrichmentJobs\" SET \"AcceptedExtractionJson\"=CAST({body} AS jsonb),\"ExtractionAcceptedAt\"={DateTimeOffset.UtcNow} WHERE \"Id\"={job.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
        Assert.Null((await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).AcceptedExtractionJson);
    }

    [MemoryPostgresFact]
    public async Task LegacyReconciliationMigrationPreservesVerifiedWorkAndRefusesUnsafeDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user,episode,_)=await SeedPartialLegacyAsync(fixture);
        await using var db=fixture.Context(); var generator=db.GetService<IMigrationsSqlGenerator>(); var migration=new ReviewedEpisodeReconciliation();
        foreach(var sql in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(sql.CommandText);
        var before=await IngestionRecovery(fixture,db).PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        Assert.False(before.CanQueue);
        foreach(var sql in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(sql.CommandText);
        var service=IngestionRecovery(fixture,db); var preview=await service.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await service.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken,preview.RequiredReconciliationPolicy));
        var queued=await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var invalid=JsonNode.Parse(queued.SourceJson)!;
        invalid["Reconciliation"]!["SourceEpisodeIds"]![0]=null;
        var invalidInput=invalid.ToJsonString();
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT csweet_episode_reconciliation_input_valid({invalidInput}) AS \"Value\"").SingleAsync());
        Assert.Equal(1,await fixture.Service(db,new ReconciliationProvider()).ProcessPendingAsync());
        var retained=JsonSerializer.Serialize(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER csweet_episode_reconciliation_guard ON \"MemoryEpisodeEnrichmentJobs\"");
        foreach(var sql in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(sql.CommandText);
        Assert.Equal(retained,JsonSerializer.Serialize(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()));
        await Assert.ThrowsAsync<PostgresException>(async()=> {
            foreach(var sql in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(sql.CommandText);
        });
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [MemoryPostgresFact]
    public async Task LegacyReconciliationMigrationRefusesUnverifiedExistingPolicyOutput()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode,_)=await SeedPartialLegacyAsync(fixture); await using var db=fixture.Context();
        var service=IngestionRecovery(fixture,db); var preview=await service.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await service.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken,preview.RequiredReconciliationPolicy));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER csweet_episode_reconciliation_guard ON \"MemoryEpisodeEnrichmentJobs\"");
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x=>x.SetProperty(j=>j.Status,MemoryCaptureStatus.Processing)
            .SetProperty(j=>j.LeaseToken,Guid.NewGuid()).SetProperty(j=>j.LeaseExpiresAt,DateTimeOffset.UtcNow.AddMinutes(5))
            .SetProperty(j=>j.Attempts,1));
        var oldOutput="{\"SchemaVersion\":4}";
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MemoryEpisodeEnrichmentJobs\" SET \"AcceptedExtractionJson\"=CAST({oldOutput} AS jsonb),\"ExtractionAcceptedAt\"=now()");
        var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards));
        Assert.Contains("verified versioned output",error.MessageText);
    }
}
