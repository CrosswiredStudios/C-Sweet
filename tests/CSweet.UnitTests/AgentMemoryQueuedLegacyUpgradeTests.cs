using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    // Exact pre-MemoryAccessAuthority ReadBindingAsync hash from platform 27f1b77.
    // This fixture has no memberships/roles; the populated source/recipient and prompt are real.
    private static async Task<string> LegacyQueuedAuthorityHashAsync(CSweetDbContext db, ChatTurn turn)
    {
        var conversation = await db.CoreConversations.AsNoTracking().SingleAsync(x => x.Id == turn.ConversationId);
        var message = await db.CoreConversationMessages.AsNoTracking().SingleAsync(x => x.Id == turn.UserMessageId);
        var senderId = message.SenderOrganizationUserId ?? conversation.InitiatedByOrganizationUserId;
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == turn.TargetAgentOrganizationUserId);
        var sender = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == senderId);
        return MemoryRecallDispatchEvidence.Hash(JsonSerializer.Serialize(new {
            conversation.Kind, conversation.AgentOrganizationUserId, conversation.InitiatedByOrganizationUserId,
            conversation.TeamId, conversation.WorkstreamId, message.Role, message.SenderOrganizationUserId, message.Content, message.CreatedAt,
            employee = new { employee.Id, employee.Revision, employee.RoleId, employee.ReportsToOrganizationUserId, employee.PermissionLevel },
            sender = new { sender.Id, sender.ApplicationUserId, sender.Revision, sender.EmployeeType, sender.RoleId, sender.ReportsToOrganizationUserId, sender.PermissionLevel },
            members = Array.Empty<object>(), roles = Array.Empty<object>() }, MemoryRecallDispatchEvidence.Json));
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedLegacyCertificateUpgradeRejectsOriginalHashWithoutRewritingAndRecoversFreshWork(bool delivered)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        var migration = new MemoryAccessAuthority(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var oldAuthority = await LegacyQueuedAuthorityHashAsync(db, turn);
        var currentReceipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(prepared.ReceiptJson, MemoryRecallDispatchEvidence.Json)!;
        var legacyReceipt = currentReceipt with { Binding = currentReceipt.Binding with { AuthorityHash = oldAuthority } };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(RecallPayload(prepared, turn));
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
        var certificate = MemoryRecallDispatchEvidence.BindPayload(MemoryRecallDispatchEvidence.Serialize(legacyReceipt),
            payloadHash, session.BusinessId, fixture.InstallationId, "chat-turn", turn.Id.ToString("D"));
        var protection = new EphemeralDataProtectionProvider();
        // Simulate the old trusted queue writer before the production generation migration exists.
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = session.BusinessId, AgentInstallationId = fixture.InstallationId,
            Kind = AgentWorkKind.Event, Name = "user-message", SourceType = "chat-turn", SourceId = turn.Id.ToString("D"),
            IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, AvailableAt = DateTimeOffset.UtcNow,
            DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10), ProtectedPayload = protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(payloadBytes),
            PayloadHash = payloadHash, MemoryRecallReceiptJson = certificate,
            Status = delivered ? AgentWorkStatus.Leased : AgentWorkStatus.Pending, AttemptCount = delivered ? 1 : 0 };
        db.AgentWorkItems.Add(work);
        if (delivered)
        {
            db.AgentWorkAttempts.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
                RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), Attempt = 1, ClaimedAt = DateTimeOffset.UtcNow,
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), LeaseTokenHash = new string('A', 64) });
            db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(), RuntimeId = Guid.Parse(session.RuntimeInstanceId),
                OrganizationId = fixture.OrganizationId, EmployeeId = fixture.EmployeeId, InstallationId = fixture.InstallationId,
                WorkId = work.Id, Attempt = 1, GrantRevision = 1, Capability = MemoryRecallDispatchEvidence.QueuedRecallCapability,
                EvidenceJson = certificate, AuthorityHash = oldAuthority, ReceiptHash = MemoryRecallDispatchEvidence.Hash("legacy-queued-certificate"), CreatedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
            await transaction.CommitAsync();
        }
        Assert.Equal(oldAuthority, await LegacyQueuedAuthorityHashAsync(db, turn)); // Ordinary snapshots are unchanged by migration.
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System);
        if (delivered)
        {
            using var wire = new InvalidatingEnrichmentWire(() => Task.CompletedTask); using var http = new HttpClient(wire);
            var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
                { TransportOverride = new HttpClientPipelineTransport(http) };
            using var client = await factory.CreateChatClientAsync(fixture.ProviderId, "test-model");
            using var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, token));
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, prepared.AgentPrompt)]));
            Assert.Equal(0, wire.Calls);
            await inbox.SettleMemoryResetAsync(Guid.Parse(session.RuntimeInstanceId), default);
            Assert.False((await inbox.ReadStateAsync(work.Id, default)).Completion!.Retryable);
            Assert.Equal(certificate, (await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync()).EvidenceJson);
            var retired = await db.AgentRuntimeInstances.SingleAsync();
            retired.TransitionTo(AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
            retired.TransitionTo(AgentRuntimeStatus.Cancelled, DateTimeOffset.UtcNow);
            retired.MemoryResetCompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        else
        {
            Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
            Assert.Equal(MemoryRecallDeliveryRejectedException.Code, (await inbox.ReadStateAsync(work.Id, default)).Completion!.FailureCode);
            Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
            Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        }
        Assert.Equal(certificate, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).MemoryRecallReceiptJson);
        Assert.Equal(AgentWorkStatus.DeadLetter, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).Status);
        var freshTurn = await SeedRecallTurnAsync(fixture);
        var fresh = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(freshTurn.Id, includeMemory: false);
        var freshWork = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event, "user-message",
            RecallPayload(fresh, freshTurn), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "chat-turn", sourceId: freshTurn.Id.ToString("D"), memoryRecallReceiptJson: fresh.ReceiptJson);
        var freshSession = DeliverySession(session);
        if (delivered)
        {
            var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId,
                TickId = Guid.NewGuid(), RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddHours(1) };
            runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
            runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
            runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
            db.AgentRuntimeInstances.Add(runtime); await db.SaveChangesAsync();
            freshSession.RuntimeInstanceId = runtime.Id; freshSession.TickId = runtime.TickId;
        }
        Assert.Equal(freshWork.Id, (await inbox.ClaimAsync(freshSession, default))!.WorkId);
        Assert.Equal(delivered ? 1 : 0, (await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).AttemptCount);
    }
}
