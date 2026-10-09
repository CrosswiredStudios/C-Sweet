using System.Text.Json;
using CSweet.Application.Core;
using System.ClientModel.Primitives;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
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
    private static JsonElement RecallPayload(PreparedMemoryRecall prepared, ChatTurn turn) => JsonSerializer.SerializeToElement(new
    {
        message = prepared.AgentPrompt, userId = prepared.Metadata!.Sender.OrganizationUserId.ToString("D"),
        conversationId = turn.ConversationId.ToString("D"), turnId = turn.Id, attempt = turn.Attempt, messageId = turn.UserMessageId,
        context = prepared.Metadata.Context, attachments = prepared.Metadata.Attachments, workContext = prepared.Metadata.WorkContext
    }, MemoryRecallDispatchEvidence.Json);
    [MemoryPostgresFact]
    public async Task RecallReceiptIsRecheckedOnActualProviderHttpRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        Assert.NotNull(prepared.Context);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        using var wire = new InvalidatingEnrichmentWire(async () =>
        {
            await using var changed = fixture.Context();
            await changed.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Revoked after send"));
        });
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(),
            NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance) { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(fixture.ProviderId, "test-model");
        var sends = 0; var denials = 0;
        using var scope = new ProviderDispatchScope(token => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), token),
            () => sends++, () => denials++);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, prepared.Context)]));
        Assert.Equal(1, wire.Calls); Assert.Equal(1, sends); Assert.Equal(1, denials);
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptRevalidatesTransferredOriginalEvidenceAndApproval()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, original) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, original));
        var preview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        var approved = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), approved.ReviewToken, "apply"));
        var conversation = await db.CoreConversations.SingleAsync(x => x.AgentOrganizationUserId == target);
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderOrganizationUserId = fixture.HumanId,
            Content = "How concise should replies be?", Role = ConversationRole.User, CreatedAt = DateTimeOffset.UtcNow };
        db.CoreConversationMessages.Add(message);
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id, UserMessageId = message.Id,
            TargetAgentOrganizationUserId = target, Attempt = 1, Status = ChatTurnStatus.RecallingMemory, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.ChatTurns.Add(turn); await db.SaveChangesAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        Assert.Contains("concise", prepared.Context);
        var installation = (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == target)).AgentInstallationId!.Value;
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId.ToString("D"), AgentInstallationId = installation,
            SourceType = "chat-turn", SourceId = turn.Id.ToString("D"), PayloadHash = "transfer-work",
            MemoryRecallReceiptJson = MemoryRecallDispatchEvidence.BindPayload(prepared.ReceiptJson, "transfer-work", fixture.OrganizationId.ToString("D"), installation, "chat-turn", turn.Id.ToString("D")) };
        var guard = new MemoryRecallDispatchEvidence(db);
        await guard.AuthorizeWorkAsync(work, target.ToString("D"), default);
        var receipt = JsonSerializer.Deserialize<MemoryRecallDispatchEvidence.Receipt>(work.MemoryRecallReceiptJson, MemoryRecallDispatchEvidence.Json)!;
        Assert.Contains(receipt.Records, x => x.Id == original.Id && x.Partition == fixture.Partition);
        var current = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), current.ReviewToken, "reject"));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, target.ToString("D"), default));
    }

    private static async Task<ChatTurn> SeedRecallTurnAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var original = await db.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId);
        var question = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = original.ConversationId,
            SenderOrganizationUserId = fixture.HumanId, Role = ConversationRole.User, Content = "What is my name?", CreatedAt = DateTimeOffset.UtcNow };
        db.CoreConversationMessages.Add(question);
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = original.ConversationId,
            UserMessageId = question.Id, TargetAgentOrganizationUserId = fixture.EmployeeId, Attempt = 1, Status = ChatTurnStatus.RecallingMemory,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.ChatTurns.Add(turn);
        var installation = await db.AgentInstallations.SingleAsync(); installation.SetupState = PluginSetupState.Ready;
        await db.SaveChangesAsync();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        return turn;
    }

    private static AgentWorkItem ReceiptWork(DurabilityFixture fixture, ChatTurn turn, string json) => new()
    {
        Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId, OrganizationId = fixture.OrganizationId.ToString("D"),
        SourceType = "chat-turn", SourceId = turn.Id.ToString("D"), PayloadHash = "test-payload",
        MemoryRecallReceiptJson = MemoryRecallDispatchEvidence.BindPayload(json, "test-payload", fixture.OrganizationId.ToString("D"),
            fixture.InstallationId, "chat-turn", turn.Id.ToString("D"))
    };

    [MemoryPostgresFact]
    public async Task RecallReceiptSurvivesNewContextAndRejectsSourceEditWithoutMemoryRewrite()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        Assert.Contains("Alice", prepared.Context); Assert.DoesNotContain("Alice", prepared.ReceiptJson);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        await using (var dispatch = fixture.Context())
            await new MemoryRecallDispatchEvidence(dispatch).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default);
        await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Corrected source"));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        // A new recall cannot turn a stale memory episode into fresh authority.
        Assert.Null((await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id)).Context);
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptBindsContentRevisionEvenWhenContentIsRestored()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Preference", "Alice likes concise answers", 1, 100, true,
            MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow.AddSeconds(-1))
            { Sensitivity = MemorySensitivity.Personal, SourceEpisodeIds = [fixture.MessageId], Confirmation = MemoryConfirmationState.Confirmed };
        await fixture.Store.WriteBlockAsync(block);
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        Assert.Contains("concise", prepared.Context);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default);
        await fixture.Store.WriteBlockAsync(block with { Content = "changed", Revision = 2 });
        await fixture.Store.WriteBlockAsync(block);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptRejectsExpiredSourcesAndRejectedDerivatives()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Preference", "Alice likes concise answers", 1, 100, true,
            MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow.AddSeconds(-1))
            { Sensitivity = MemorySensitivity.Personal, SourceEpisodeIds = [fixture.MessageId], Confirmation = MemoryConfirmationState.Confirmed };
        await fixture.Store.WriteBlockAsync(block);
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        Assert.Contains("concise", prepared.Context);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        await fixture.Store.WriteBlockAsync(block with { Confirmation = MemoryConfirmationState.Rejected });
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        var episode = (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Single();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET expires_at={DateTimeOffset.UtcNow.AddSeconds(-1)}, payload=jsonb_set(payload,'{{expiresAt}}',to_jsonb({DateTimeOffset.UtcNow.AddSeconds(-1)})) WHERE id={episode.Id}");
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        Assert.Null((await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id)).Context);
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptBindsRecipientRoleTeamTurnAttemptAndPayload()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson); var guard = new MemoryRecallDispatchEvidence(db);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, Guid.NewGuid().ToString("D"), default));
        work.PayloadHash = "different";
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        work.PayloadHash = "test-payload";
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId);
        employee.ReportsToOrganizationUserId = fixture.HumanId; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id); work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        var role = new Role { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, Name = "New role" };
        db.CoreRoles.Add(role); employee.RoleId = role.Id; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id); work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, Name = "Team", TeamKey = "team",
            NormalizedName = "TEAM", LeadOrganizationUserId = fixture.HumanId };
        db.OrganizationTeams.Add(team); db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            TeamId = team.Id, OrganizationUserId = fixture.EmployeeId, ExclusiveAgentEmployeeId = fixture.EmployeeId }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id); work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        team.LeadOrganizationUserId = fixture.EmployeeId; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id); work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        var account = new CSweet.Infrastructure.Auth.ApplicationUser { Id = Guid.NewGuid(), UserName = "replacement-account" };
        db.Users.Add(account);
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).ApplicationUserId = account.Id;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id); work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempt, 2));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptIsSavedAtomicallyWithWorkAndParticipatesInIdempotency()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var payload = RecallPayload(prepared, turn);
        Task<AgentWorkItem> Enqueue(string? receipt) => inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId,
            AgentWorkKind.Event, "user-message", payload, "receipt-test", DateTimeOffset.UtcNow.AddMinutes(10), sourceType: "chat-turn",
            sourceId: turn.Id.ToString("D"), memoryRecallReceiptJson: receipt);
        var work = await Enqueue(prepared.ReceiptJson); Assert.Equal(work.Id, (await Enqueue(prepared.ReceiptJson)).Id);
        await using (var fresh = fixture.Context())
        {
            var persisted = await fresh.AgentWorkItems.SingleAsync(); Assert.NotNull(persisted.MemoryRecallReceiptJson);
            await new MemoryRecallDispatchEvidence(fresh).AuthorizeWorkAsync(persisted, fixture.EmployeeId.ToString("D"), default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Enqueue(null));
        await using (var tamper = fixture.Context())
        {
            (await tamper.AgentWorkItems.SingleAsync()).MemoryRecallReceiptJson = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => tamper.SaveChangesAsync());
        }
        Assert.Single(await db.AgentWorkItems.ToListAsync());
        var migration = new WorkMemoryRecallEvidence(); var generator = db.GetService<IMigrationsSqlGenerator>();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptMigrationPreservesLegacyWorkButDeniesItsDispatch()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var migration = new WorkMemoryRecallEvidence(); var generator = db.GetService<IMigrationsSqlGenerator>();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var work = await inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InstallationId, AgentWorkKind.Event, "user-message",
            JsonSerializer.SerializeToElement(new { message = "old context" }), "legacy", DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "chat-turn", sourceId: turn.Id.ToString("D"));
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        db.ChangeTracker.Clear();
        work = await db.AgentWorkItems.SingleAsync();
        Assert.Null(work.MemoryRecallReceiptJson);
        Assert.Equal("legacy", work.IdempotencyKey);
        Assert.NotEmpty(work.ProtectedPayload);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        var noMemory = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        Assert.Null(noMemory.Context);
        await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(ReceiptWork(fixture, turn, noMemory.ReceiptJson), fixture.EmployeeId.ToString("D"), default);
    }

    [MemoryPostgresFact]
    public async Task RecallReceiptRejectsUnavailableAndExcludedSources()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id);
        var work = ReceiptWork(fixture, turn, prepared.ReceiptJson);
        var conversation = await db.CoreConversations.SingleAsync(); conversation.ArchivedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        conversation.ArchivedAt = null; await db.SaveChangesAsync();
        db.MemoryCaptureExclusions.Add(new MemoryCaptureExclusion { SourceMessageId = fixture.MessageId, OrganizationId = fixture.OrganizationId,
            EmployeeId = fixture.EmployeeId, TriggerJobId = Guid.NewGuid(), ReasonCode = "memory_source_invalidated", ExcludedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={fixture.MessageId}");
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
    }
}
