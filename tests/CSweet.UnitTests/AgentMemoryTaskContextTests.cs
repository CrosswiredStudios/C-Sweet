using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static Task<AgentWorkItem> QueueOwnTaskNotification(AgentWorkInbox inbox, DurabilityFixture fixture) =>
        inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId, AgentWorkKind.Event,
            "com.csweet.work.personal-todo.available.v1", JsonSerializer.SerializeToElement(new
            { ownerId = fixture.EmployeeId, boardId = Guid.NewGuid(), itemId = Guid.NewGuid() }),
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(20),
            sourceType: "platform-event", sourceId: Guid.NewGuid().ToString("D"));

    [MemoryPostgresTheory]
    [InlineData("unchanged")]
    [InlineData("historical-source")]
    [InlineData("historical-capacity")]
    public async Task TaskContextChatToOwnNotificationKeepsRuntimeAndDoesNotInheritHistoricalReads(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(f); var (session, initial) = await SeedBrokerReadLeaseAsync(f);
        await using var db = f.Context();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(f, db, turn, includeMemory: false);
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        await SeedReviewClaim(f);
        var read = await ReadHandler(f, db).HandleAsync(session, ReadSearch(f), default);
        Assert.True(read.Succeeded, read.Error);
        Assert.Contains("concise", read.Payload.ToStringUtf8());
        var notification = await QueueOwnTaskNotification(inbox, f);
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        await inbox.CompleteAsync(DeliverySession(session), work.Id, claim.Attempt, claim.LeaseToken,
            new(true, JsonSerializer.SerializeToElement(new { reply = "Pitch and approval created" }), null), default);
        if (change == "historical-source")
            await db.CoreConversations.Where(x => x.Id == turn.ConversationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        if (change == "historical-capacity")
        {
            var old = await db.AgentMemoryReadReceipts.AsNoTracking().FirstAsync(x => x.WorkId == work.Id);
            for (var i = 0; i < 128; i++) db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt
            { Id = Guid.NewGuid(), RuntimeId = old.RuntimeId, WorkId = old.WorkId, Attempt = old.Attempt,
                OrganizationId = old.OrganizationId, EmployeeId = old.EmployeeId, InstallationId = old.InstallationId,
                GrantRevision = old.GrantRevision, Capability = old.Capability, EvidenceJson = old.EvidenceJson,
                AuthorityHash = old.AuthorityHash, ReceiptHash = (i + 100).ToString("D64") });
            await db.SaveChangesAsync();
        }
        var next = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        Assert.Equal(notification.Id, next.WorkId);
        await using var reconnect = f.Context();
        await new PlatformMemoryReadEvidence(reconnect).AuthorizeDispatchAsync(session, notification.Id, default, next.Attempt);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() =>
            new PlatformMemoryReadEvidence(reconnect).AuthorizeDispatchAsync(session, work.Id, default, claim.Attempt));
        Assert.Null((await reconnect.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.Empty(await reconnect.AgentRuntimeEvents.ToArrayAsync());
        Assert.NotEmpty(await reconnect.AgentMemoryReadReceipts.Where(x => x.WorkId == work.Id).ToArrayAsync());
        Assert.Empty(await reconnect.AgentMemoryReadReceipts.Where(x => x.WorkId == notification.Id).ToArrayAsync());
        Assert.True((await inbox.ReadStateAsync(work.Id, default)).Completion!.Succeeded);
    }

    [MemoryPostgresFact]
    public async Task TaskContextOldAttemptCannotDispatchOrChargeTheNewAttempt()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(f); await SeedReviewClaim(f);
        await using var db = f.Context();
        Assert.True((await ReadHandler(f, db).HandleAsync(session, ReadSearch(f), default)).Succeeded);
        // Fixture-only retry state: retain immutable first-attempt evidence, replace its live lease.
        await db.AgentWorkAttempts.ExecuteUpdateAsync(s => s.SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow));
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AttemptCount, 2));
        db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId),
            Attempt = 2, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default, 2);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default, 1));
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.True((await ReadHandler(f, db).HandleAsync(session, ReadSearch(f), default)).Succeeded);
        Assert.Equal(new[] { 1, 2 }, await db.AgentMemoryReadReceipts.OrderBy(x => x.Attempt).Select(x => x.Attempt).ToArrayAsync());
    }

    [MemoryPostgresFact]
    public async Task TaskContextReceiptIndexUpgradePreservesEvidenceAndDoesNotCertifyAnOlderRuntime()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(f); await SeedReviewClaim(f);
        await using var db = f.Context();
        Assert.True((await ReadHandler(f, db).HandleAsync(session, ReadSearch(f), default)).Succeeded);
        var old = await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync();
        await db.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryReadEvidenceVersion, 3));
        var migration = new TaskScopedMemoryReadEvidence(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var after = await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(old.EvidenceJson, after.EvidenceJson); Assert.Equal(old.ReceiptHash, after.ReceiptHash);
        Assert.Equal(3, (await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryReadEvidenceVersion);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("20261009174500_TaskScopedMemoryReadEvidence", db.Database.GetMigrations().Last());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default, 1));
        Assert.Equal(MemoryRuntimeResetRequiredException.LegacyEvidence, (await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetReasonCode);
    }

    private sealed class TaskContextWire(Func<Task> finish) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (Bodies.Count == 1)
            {
                await finish();
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{\"error\":{\"message\":\"retry\",\"type\":\"server_error\"}}", Encoding.UTF8, "application/json") };
                response.Headers.TryAddWithoutValidation("Retry-After", "0"); return response;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"id":"chatcmpl-task","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}
                """, Encoding.UTF8, "application/json") };
        }
    }

    [MemoryPostgresFact]
    public async Task TaskContextLateHttpRetryCannotResetTheNextNotificationOrCarryItsPreviousPrompt()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(f); var (session, initial) = await SeedBrokerReadLeaseAsync(f);
        await using var db = f.Context();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(f, db, turn, includeMemory: false);
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        var notification = await QueueOwnTaskNotification(inbox, f);
        ClaimedAgentWork? next = null;
        using var wire = new TaskContextWire(async () =>
        {
            await inbox.CompleteAsync(DeliverySession(session), work.Id, claim.Attempt, claim.LeaseToken, new(true, null, null), default);
            next = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(DeliverySession(session), default));
        });
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(f.ProviderId, "test-model");
        using (var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, token, claim.Attempt)))
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "private-first-prompt-only")]));
        Assert.Single(wire.Bodies); Assert.NotNull(next); Assert.Equal(notification.Id, next.WorkId);
        using (var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, notification.Id, token, next.Attempt)))
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Review your own task notification.")]);
        Assert.Equal(2, wire.Bodies.Count);
        Assert.DoesNotContain("private-first-prompt-only", wire.Bodies[1]);
        Assert.Contains("Review your own task notification", wire.Bodies[1]);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.Equal(AgentWorkStatus.Leased, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == notification.Id)).Status);
    }
}
