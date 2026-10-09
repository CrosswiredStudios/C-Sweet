using System.ClientModel.Primitives;
using System.Data.Common;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    public static IEnumerable<object[]> QueuedSenderAuthorityChanges() =>
        new[] { "enabled", "revision", "scope", "grant", "grant-recreated", "assignment", "package" }
            .Select(change => new object[] { change });

    private sealed record SenderQueue(AgentSession Session, AgentWorkInbox Inbox, AgentWorkItem Work, Guid Sender, Guid SenderInstallation);

    private static async Task<SenderQueue> SeedAgentSenderQueueAsync(DurabilityFixture fixture, CSweetDbContext db)
    {
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var recipient = await db.AgentInstallations.AsNoTracking().SingleAsync();
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
            BusinessId = session.BusinessId, PackageVersionId = recipient.PackageVersionId };
        var sender = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id, DisplayName = "Sending agent" };
        var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            Kind = ConversationKind.AgentChannel, AgentOrganizationUserId = fixture.EmployeeId, InitiatedByOrganizationUserId = sender.Id };
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id,
            Role = ConversationRole.User, SenderOrganizationUserId = sender.Id, Content = "Please review the production brief." };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id,
            TargetAgentOrganizationUserId = fixture.EmployeeId, UserMessageId = message.Id, Status = ChatTurnStatus.RecallingMemory };
        db.AddRange(installation, sender, conversation, message, turn,
            new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, RequiredCapabilitiesJson = "[\"source-capability\"]" });
        await db.SaveChangesAsync();
        var prepared = await fixture.Service(db, new UsageProviderFactory()).PrepareTurnRecallAsync(turn.Id, includeMemory: false);
        Assert.Equal("Agent", prepared.Metadata!.Sender.EmployeeType);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var work = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Event, "user-message",
            RecallPayload(prepared, turn), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(10),
            sourceType: "chat-turn", sourceId: turn.Id.ToString("D"), memoryRecallReceiptJson: prepared.ReceiptJson);
        return new(session, inbox, work, sender.Id, installation.Id);
    }

    private static async Task RestoreSenderInstallationAsync(DurabilityFixture fixture, SenderQueue queued, string change)
    {
        await using var db = fixture.Context();
        var id = queued.SenderInstallation;
        switch (change)
        {
            case "enabled":
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true)); break;
            case "revision":
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevisionStatus, PluginRevisionStatus.Retired));
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevisionStatus, PluginRevisionStatus.Active)); break;
            case "scope":
                var scope = await db.AgentInstallations.Where(x => x.Id == id).Select(x => x.Scope).SingleAsync();
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Scope, (PluginInstallationScope)((int)scope + 1)));
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Scope, scope)); break;
            case "grant":
                await db.AgentInstallationGrants.Where(x => x.AgentInstallationId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequiredCapabilitiesJson, "[]"));
                await db.AgentInstallationGrants.Where(x => x.AgentInstallationId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequiredCapabilitiesJson, "[\"source-capability\"]")); break;
            case "grant-recreated":
                var grant = await db.AgentInstallationGrants.AsNoTracking().SingleAsync(x => x.AgentInstallationId == id);
                await db.AgentInstallationGrants.Where(x => x.Id == grant.Id).ExecuteDeleteAsync();
                db.AgentInstallationGrants.Add(grant); await db.SaveChangesAsync(); break;
            case "assignment":
                await db.CoreOrganizationUsers.Where(x => x.Id == queued.Sender).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentInstallationId, (Guid?)null));
                await db.CoreOrganizationUsers.Where(x => x.Id == queued.Sender).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentInstallationId, id)); break;
            case "package":
                var originalPackage = await db.AgentInstallations.Where(x => x.Id == id).Select(x => x.PackageVersionId).SingleAsync();
                var packageSource = await db.AgentPackageVersions.Where(x => x.Id == originalPackage).Select(x => x.PackageSourceId).SingleAsync();
                var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = packageSource,
                    AgentId = "test.sender", AgentName = "Sender", Version = "2.0.0", CommitSha = Guid.NewGuid().ToString("N") };
                db.AgentPackageVersions.Add(package); await db.SaveChangesAsync();
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageVersionId, package.Id));
                await db.AgentInstallations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageVersionId, originalPackage)); break;
            default: throw new ArgumentException(nameof(change));
        }
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(QueuedSenderAuthorityChanges))]
    public async Task QueuedSenderAuthorityRestorationBeforeClaimRejectsOriginalCertificate(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); var queued = await SeedAgentSenderQueueAsync(fixture, db);
        await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(queued.Work, fixture.EmployeeId.ToString("D"), default);
        var original = queued.Work.MemoryRecallReceiptJson;
        await RestoreSenderInstallationAsync(fixture, queued, change);
        Assert.Null(await queued.Inbox.ClaimAsync(DeliverySession(queued.Session), default));
        await using var verify = fixture.Context();
        var work = await verify.AgentWorkItems.SingleAsync(x => x.Id == queued.Work.Id);
        Assert.Equal(AgentWorkStatus.DeadLetter, work.Status); Assert.Equal(0, work.AttemptCount);
        Assert.Equal(original, work.MemoryRecallReceiptJson);
        Assert.Empty(await verify.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Null((await verify.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(QueuedSenderAuthorityChanges))]
    public async Task QueuedSenderAuthorityRestorationBeforeProviderSendsNoHttp(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); var queued = await SeedAgentSenderQueueAsync(fixture, db);
        var claimed = Assert.IsType<ClaimedAgentWork>(await queued.Inbox.ClaimAsync(DeliverySession(queued.Session), default));
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(queued.Session, queued.Work.Id, default);
        await RestoreSenderInstallationAsync(fixture, queued, change);
        using var wire = new InvalidatingEnrichmentWire(() => Task.CompletedTask);
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(fixture.ProviderId, "test-model");
        using var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(queued.Session, queued.Work.Id, token));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, claimed.Payload.GetProperty("message").GetString())]));
        Assert.Equal(0, wire.Calls);
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(QueuedSenderAuthorityChanges))]
    public async Task QueuedSenderAuthorityRestorationDuringHttpRetryStopsSecondSend(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); var queued = await SeedAgentSenderQueueAsync(fixture, db);
        var claimed = Assert.IsType<ClaimedAgentWork>(await queued.Inbox.ClaimAsync(DeliverySession(queued.Session), default));
        using var wire = new InvalidatingEnrichmentWire(() => RestoreSenderInstallationAsync(fixture, queued, change));
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(fixture.ProviderId, "test-model");
        using var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(queued.Session, queued.Work.Id, token));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, claimed.Payload.GetProperty("message").GetString())]));
        Assert.Equal(1, wire.Calls);
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task QueuedSenderAuthorityOrdinaryUpdatesAndRolledBackRevocationRemainAuthorized()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); var queued = await SeedAgentSenderQueueAsync(fixture, db);
        Assert.NotNull(await queued.Inbox.ClaimAsync(DeliverySession(queued.Session), default));
        await db.AgentInstallations.Where(x => x.Id == queued.SenderInstallation)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConfigurationSyncLastAttemptAt, DateTimeOffset.UtcNow));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.AgentInstallations.Where(x => x.Id == queued.SenderInstallation).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
            await transaction.RollbackAsync();
        }
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(queued.Session, queued.Work.Id, default);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("unlinked")]
    [InlineData("disabled")]
    [InlineData("retired")]
    [InlineData("foreign")]
    [InlineData("human-linked")]
    [InlineData("unknown-kind")]
    [InlineData("recipient-retired")]
    public async Task QueuedSenderAuthorityRejectsUnavailableAndInconsistentIdentitiesAtPreparation(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); var queued = await SeedAgentSenderQueueAsync(fixture, db);
        if (change == "unlinked")
            await db.CoreOrganizationUsers.Where(x => x.Id == queued.Sender).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentInstallationId, (Guid?)null));
        else if (change == "disabled")
            await db.AgentInstallations.Where(x => x.Id == queued.SenderInstallation).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
        else if (change is "retired" or "recipient-retired")
            await db.AgentInstallations.Where(x => x.Id == (change == "retired" ? queued.SenderInstallation : fixture.InstallationId))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevisionStatus, PluginRevisionStatus.Retired));
        else if (change == "foreign")
            await db.AgentInstallations.Where(x => x.Id == queued.SenderInstallation).ExecuteUpdateAsync(s => s.SetProperty(x => x.BusinessId, Guid.NewGuid().ToString("D")));
        else await db.CoreOrganizationUsers.Where(x => x.Id == queued.Sender).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.EmployeeType, change == "human-linked" ? EmployeeType.Human : (EmployeeType)99));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => fixture.Service(db, new UsageProviderFactory())
            .PrepareTurnRecallAsync(Guid.Parse(queued.Work.SourceId!), includeMemory: false));
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Empty(await db.AgentWorkAttempts.ToArrayAsync());
    }

    private sealed class QueuedGenerationReadFailure : DbCommandInterceptor
    {
        public Exception? Failure { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Failure is { } error && command.CommandText.Contains("\"MemoryAccessAuthority\"", StringComparison.Ordinal))
            {
                Failure = null;
                throw error;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [MemoryPostgresFact]
    public async Task QueuedSenderAuthorityGenerationOutageSendsNoHttpWithoutResettingValidRetainedWork()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var failure = new QueuedGenerationReadFailure();
        await using var db = fixture.Context(failure);
        var queued = await SeedAgentSenderQueueAsync(fixture, db);
        var claimed = Assert.IsType<ClaimedAgentWork>(await queued.Inbox.ClaimAsync(DeliverySession(queued.Session), default));
        var originalReceipt = (await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync()).EvidenceJson;
        using var wire = new InvalidatingEnrichmentWire(() => Task.CompletedTask);
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(fixture.ProviderId, "test-model");
        using var scope = new ProviderDispatchScope(token => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(queued.Session, queued.Work.Id, token));
        foreach (var error in new Exception[] { new TimeoutException("injected_generation_timeout"), new Npgsql.NpgsqlException("injected_generation_database_failure") })
        {
            failure.Failure = error;
            // The provider boundary returns a content-free denial for infrastructure failures.
            // It must neither send HTTP nor classify the outage as invalid retained evidence.
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, claimed.Payload.GetProperty("message").GetString())]));
            Assert.Null(failure.Failure);
            Assert.Equal(0, wire.Calls);
            await using var verify = fixture.Context();
            Assert.Null((await verify.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
            Assert.Null((await verify.AgentWorkAttempts.SingleAsync()).FinishedAt);
            Assert.Equal(AgentWorkStatus.Leased, (await verify.AgentWorkItems.SingleAsync()).Status);
            Assert.Equal(originalReceipt, (await verify.AgentMemoryReadReceipts.SingleAsync()).EvidenceJson);
        }
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(queued.Session, queued.Work.Id, default);
    }
}
