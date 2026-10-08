using System.ClientModel.Primitives;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceBindsTransferPackageStateAndSources()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        var ns = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"), fixture.EmployeeId.ToString("D"), fixture.HumanId.ToString("D"), "csweet");
        var projection = MemoryReadProjection.Create(await fixture.Store.ExportAsync(fixture.Partition), fixture.Partition, MemorySensitivity.Personal, DateTimeOffset.UtcNow);
        var item = MemoryReadProjection.TransferItems(projection, fixture.Partition).Single(x => x.MemoryId == claim.Id);
        var package = new KnowledgeTransferPackage(Guid.NewGuid(), session.BusinessId, fixture.EmployeeId.ToString("D"), fixture.EmployeeId.ToString("D"),
            [ns], ns, "An explicitly shared preference", [item], MemorySensitivity.Personal, KnowledgeTransferStatus.PendingApproval, DateTimeOffset.UtcNow, fixture.HumanId.ToString("D"));
        await ((IKnowledgeTransferStore)fixture.Store).WriteKnowledgeTransferAsync(package);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var result = await ReadHandler(fixture, db).HandleAsync(session, ReadRequest("get-knowledge-transfer", new { packageId = package.Id }), default);
        Assert.True(result.Succeeded, result.Error); Assert.Contains("concise", result.Payload.ToStringUtf8());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await ((IKnowledgeTransferStore)fixture.Store).WriteKnowledgeTransferAsync(package with { Status = KnowledgeTransferStatus.Rejected });
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceBoundedRuntimeCannotDropOldReceiptsToAcceptMoreData()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture); await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var handler = ReadHandler(fixture, db);
        Assert.True((await handler.HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        var existing = await db.AgentMemoryReadReceipts.SingleAsync();
        for (var i = 1; i < 64; i++) db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(),
            OrganizationId = existing.OrganizationId, EmployeeId = existing.EmployeeId, InstallationId = existing.InstallationId,
            RuntimeId = existing.RuntimeId, WorkId = existing.WorkId, Attempt = 1, GrantRevision = 1, Capability = existing.Capability,
            EvidenceJson = existing.EvidenceJson, AuthorityHash = existing.AuthorityHash, ReceiptHash = i.ToString("D64") });
        await db.SaveChangesAsync();
        var denied = await handler.HandleAsync(session, ReadRequest("export", fixture.Partition, CSweetMemoryCapabilities.Export), default);
        Assert.False(denied.Succeeded); Assert.Equal(64, await db.AgentMemoryReadReceipts.CountAsync());
        db.AgentMemoryReadReceipts.Remove(existing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static readonly JsonSerializerOptions ReadEvidenceJson = new(JsonSerializerDefaults.Web);

    private static async Task<(AgentSession Session, AgentWorkItem Work)> SeedBrokerReadLeaseAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var capabilities = new HashSet<string>([CSweetMemoryCapabilities.Query, CSweetMemoryCapabilities.Export, CSweetMemoryCapabilities.Write, CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream]);
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId, TickId = Guid.NewGuid(), RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddHours(1) };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
        db.AgentRuntimeInstances.Add(runtime);
        db.AgentInstallationGrants.Add(new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId,
            GrantRevision = 1, RequiredCapabilitiesJson = JsonSerializer.Serialize(capabilities) });
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId.ToString("D"), AgentInstallationId = fixture.InstallationId,
            Kind = AgentWorkKind.Capability, Name = "fixture", IdempotencyKey = Guid.NewGuid().ToString("N"), Status = AgentWorkStatus.Leased,
            DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(20), AttemptCount = 1 };
        db.AgentWorkItems.Add(work);
        db.AgentWorkAttempts.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id,
            Attempt = 1, ClaimedAt = DateTimeOffset.UtcNow, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) });
        await db.SaveChangesAsync(); await fixture.Store.InitializeAsync();
        return (new(Guid.NewGuid().ToString("N"), "memory.test", fixture.InstallationId.ToString("D"), fixture.OrganizationId.ToString("D"),
            runtime.Id.ToString("D"), runtime.TickId.ToString("D"), new(new HashSet<string>(), new HashSet<string>(), capabilities, 1)), work);
    }

    private static PlatformMemoryCapabilityHandler ReadHandler(DurabilityFixture fixture, CSweetDbContext db, IPlatformMemoryReadEvidence? tracker = null) =>
        new(fixture.Store, NullLogger<PlatformMemoryCapabilityHandler>.Instance, new AgentMemoryIdentityResolver(db), tracker ?? new PlatformMemoryReadEvidence(db));
    private static RequestCapability ReadRequest(string operation, object payload, string capability = CSweetMemoryCapabilities.Query) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), Capability = capability, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new CSweetMemoryCommand(operation, JsonSerializer.SerializeToElement(payload, ReadEvidenceJson)), ReadEvidenceJson))
    };
    private static RequestCapability ReadSearch(DurabilityFixture fixture) => ReadRequest("search", new MemorySearchRequest(fixture.Partition, MemoryScope.User, "Alice"));

    [MemoryPostgresFact]
    public async Task BrokerReadEvidencePersistsBeforeReturnDeduplicatesAndSurvivesRestart()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var handler = ReadHandler(fixture, db);
        var result = await handler.HandleAsync(session, ReadSearch(fixture), default);
        Assert.True(result.Succeeded, result.Error); Assert.Contains("Alice", result.Payload.ToStringUtf8());
        Assert.True((await handler.HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        var receipt = Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        Assert.Equal(work.Id, receipt.WorkId); Assert.Equal(fixture.EmployeeId, receipt.EmployeeId); Assert.DoesNotContain("concise replies", receipt.EvidenceJson);
        await using var restart = fixture.Context(); await restart.Database.OpenConnectionAsync();
        await new PlatformMemoryReadEvidence(restart).AuthorizeDispatchAsync(session, work.Id, default);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Rejected);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(restart).AuthorizeDispatchAsync(session, work.Id, default));
        receipt.EvidenceJson = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceCoversEntityClaimListsAndAllExportRecordTypes()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var subject = (await fixture.Store.ExportAsync(fixture.Partition)).Entities.Single();
        var other = subject with { Id = Guid.NewGuid(), CanonicalName = "Bob", ApplicationKey = "person:bob" };
        await fixture.Store.UpsertEntityAsync(other);
        await fixture.Store.WriteEdgeAsync(new(Guid.NewGuid(), fixture.Partition, claim.EpisodeId, subject.Id, "knows", other.Id, MemoryTrustTier.AgentInference, 1, now, null, false, now));
        await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Preferences", "Alice prefers concise replies", 1, 100, true, MemoryTrustTier.AgentInference, now)
            { SourceEpisodeIds = [claim.EpisodeId], Sensitivity = MemorySensitivity.Personal, Confirmation = MemoryConfirmationState.Confirmed });
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(), fixture.Partition, claim.EpisodeId, "Concise response", "Keep the answer concise.", "When answering Alice", 1,
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, now, null, now));
        await fixture.Store.WriteEmbeddingAsync(new(Guid.NewGuid(), fixture.Partition, claim.EpisodeId, MemoryLayer.Episodic, [1f, 0f], "test", now));
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync(); var handler = ReadHandler(fixture, db);
        foreach (var request in new[] { ReadRequest("find-entity", new { partition = fixture.Partition, canonicalName = "Alice" }),
            ReadRequest("find-entity-by-application-key", new { partition = fixture.Partition, applicationKey = "person:bob" }),
            ReadRequest("get-claim", new { claimId = claim.Id }), ReadRequest("list-claims", fixture.Partition),
            ReadRequest("export", fixture.Partition, CSweetMemoryCapabilities.Export) })
        {
            var result = await handler.HandleAsync(session, request, default); Assert.True(result.Succeeded, result.Error);
            await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        }
        Assert.Equal(4, await db.AgentMemoryReadReceipts.CountAsync()); // get/list have the same one-claim evidence.
        await fixture.Store.UpsertEntityAsync(subject with { Aliases = ["Changed alias"] });
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceRejectsMissingAndAmbiguousLeasesAndRevokedGrant()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync(); var handler = ReadHandler(fixture, db);
        var attempt = await db.AgentWorkAttempts.SingleAsync(); attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.False((await handler.HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        db.AgentWorkAttempts.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = attempt.RuntimeInstanceId,
            Attempt = 2, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) }); await db.SaveChangesAsync();
        Assert.False((await handler.HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        await db.AgentWorkAttempts.Where(x => x.Attempt == 2).ExecuteDeleteAsync();
        (await db.AgentInstallationGrants.SingleAsync()).GrantRevision++; await db.SaveChangesAsync();
        var denied = await handler.HandleAsync(session, ReadSearch(fixture), default);
        Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode); Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    private sealed class ReadReceiptSaveFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AgentMemoryReadReceipt>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("injected_receipt_failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceCommitFailureReturnsNoPrivatePayload()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture); await SeedReviewClaim(fixture);
        await using var db = fixture.Context(new ReadReceiptSaveFailure()); await db.Database.OpenConnectionAsync();
        var result = await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default);
        Assert.False(result.Succeeded); Assert.DoesNotContain("Alice", result.Payload.ToStringUtf8());
        await using var fresh = fixture.Context(); Assert.Empty(await fresh.AgentMemoryReadReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceConcurrentDuplicateReadsShareOneReceipt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture); await SeedReviewClaim(fixture);
        async Task<CapabilityResult> Read()
        {
            await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
            return await ReadHandler(fixture, db).HandleAsync(session with { }, ReadSearch(fixture), default);
        }
        var results = await Task.WhenAll(Read(), Read()); Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
        await using var verify = fixture.Context(); Assert.Single(await verify.AgentMemoryReadReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceSurvivesWorkDeletionAndBlocksCrossRecipientReuse()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Human };
        var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, AgentOrganizationUserId = fixture.EmployeeId, InitiatedByOrganizationUserId = human.Id };
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ConversationRole.User, SenderOrganizationUserId = human.Id, Content = "Another person's chat" };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id, TargetAgentOrganizationUserId = fixture.EmployeeId, UserMessageId = message.Id };
        db.AddRange(human, conversation, message, turn);
        var next = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId, OrganizationId = fixture.OrganizationId.ToString("D"),
            SourceType = "chat-turn", SourceId = turn.Id.ToString("D"), Status = AgentWorkStatus.Leased, DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        db.AgentWorkItems.Add(next); db.AgentWorkAttempts.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = next.Id, RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId),
            Attempt = 1, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, next.Id, default));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, null, default));
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceRevocationStopsActualHttpRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        using var wire = new InvalidatingEnrichmentWire(async () =>
        {
            await using var change = fixture.Context();
            await change.CoreConversations.Where(x => x.AgentOrganizationUserId == fixture.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        });
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(fixture.ProviderId, "test-model");
        using var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, token));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "recalled private preference")]));
        Assert.Equal(1, wire.Calls);
    }

    [MemoryPostgresFact]
    public async Task BrokerReadEvidenceMigrationRequiresFreshRuntimeAndProtectsDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var migration = new BrokerMemoryReadEvidence(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(0, (await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryReadEvidenceVersion);
        Assert.False((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        // Use a separate runtime and work attempt: the fenced runtime never resumes.
        await db.AgentWorkAttempts.ExecuteDeleteAsync();
        await db.AgentWorkItems.ExecuteDeleteAsync();
        await db.AgentRuntimeInstances.ExecuteDeleteAsync();
        await db.AgentInstallationGrants.ExecuteDeleteAsync();
        (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("memory_read_evidence_downgrade_requires_snapshot", error.MessageText);
        await db.AgentWorkAttempts.Where(x => x.RuntimeInstanceId == Guid.Parse(session.RuntimeInstanceId)).ExecuteDeleteAsync();
        await db.AgentRuntimeInstances.Where(x => x.Id == Guid.Parse(session.RuntimeInstanceId)).ExecuteDeleteAsync();
        Assert.Empty(await db.AgentMemoryReadReceipts.ToListAsync());
    }
}
