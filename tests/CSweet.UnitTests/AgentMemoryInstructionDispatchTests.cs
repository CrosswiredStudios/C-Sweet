using System.Runtime.CompilerServices;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Contracts.Memory;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using CSweet.Application.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed class InstructionDispatchProvider : ILlmProviderFactory
    {
        internal string? Context;
        internal int Sends;
        internal Exception? Failure;
        internal Func<Task>? BeforeClient;
        internal Func<Task>? BeforeResponse;
        internal Func<Task>? BeforeRetry;
        public async Task<IChatClient> CreateChatClientAsync(Guid id, CancellationToken token = default)
        {
            if (BeforeClient is not null) await BeforeClient();
            return new InstructionDispatchClient(this);
        }
    }

    private sealed class InstructionDispatchLogger(InstructionDispatchProvider provider) : ILogger<PlatformLlmCapabilityHandler>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) provider.Failure = exception; }
    }

    private sealed class InstructionDispatchClient(InstructionDispatchProvider owner) : DelegatingChatClient(new UsageChatClient())
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            owner.Context = options?.Instructions;
            owner.Sends++;
            if (owner.BeforeRetry is not null)
            {
                await owner.BeforeRetry();
                await ProviderDispatchScope.AuthorizeCurrentAsync(cancellationToken);
                owner.Sends++;
            }
            if (owner.BeforeResponse is not null) await owner.BeforeResponse();
            yield return new(ChatRole.Assistant, [new TextContent("Completed the requested paddle change.")]);
        }
    }

    private sealed class InstructionDispatchMedia : IMediaAssetService
    {
        public Task<MediaAssetResponse> SaveUploadAsync(Guid organizationId, string fileName, string contentType,
            Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MediaAssetResponse?> GetAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult<MediaAssetResponse?>(null);
        public Task<(MediaAssetResponse Asset, Stream Content)?> OpenReadAsync(Guid id, Guid organizationId,
            CancellationToken cancellationToken = default) => Task.FromResult<(MediaAssetResponse Asset, Stream Content)?>(null);
        public Task DeleteAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<CapabilityResult[]> RunInstructionModelAsync(DurabilityFixture f, AgentSession session,
        AgentWorkItem work, ILlmProviderFactory provider, bool attributed = true, string? callerInstructions = null)
    {
        await using var db = f.Context();
        var handler = new PlatformLlmCapabilityHandler(db, provider, new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], new InstructionDispatchMedia(),
            provider is InstructionDispatchProvider diagnostic ? new InstructionDispatchLogger(diagnostic) : NullLogger<PlatformLlmCapabilityHandler>.Instance);
        using var scope = attributed ? new InferenceExecutionAttribution(work.Id, Guid.NewGuid(), 1).Enter() : null;
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString("D"), Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(new { providerProfileId = f.ProviderId, instructions = callerInstructions,
                messages = new[] { new { role = "user", text = "Carry out the current work." } },
                caseId = work.Id, workItemId = work.Id }) }; // Caller hints are deliberately wrong.
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.StreamAsync(session, request, default)) results.Add(result);
        return results.ToArray();
    }

    private static async Task<(InstructionSetup Setup, Guid Comment, AgentSession Session, AgentWorkItem Work)>
        PrepareInstructionDispatchAsync(DurabilityFixture f, string consumer = "coordination", bool publish = true)
    {
        var setup = await PrepareInstructionAsync(f);
        var published = publish ? await PublishInstructionAsync(f, setup) : null;
        await GrantInstructionEditsAsync(f, setup);
        var (session, work) = await SeedBrokerReadLeaseAsync(f);
        var scoped = new ScopedAudienceFixture(setup.User, default!, setup.Selection.ConversationId, setup.Item);
        if (consumer == "coordination") await SeedScopedCoordinationAsync(f, scoped, work.Id);
        if (consumer == "stage") await SeedScopedStageConsumerAsync(f, scoped, work.Id);
        await using var db = f.Context();
        (await db.LlmProviderProfiles.SingleAsync()).DefaultChatModel = "test-model";
        // Automatic platform context needs only the approved model capability, not caller-issued memory reads.
        ((HashSet<string>)session.Grant.RequestedCapabilities).Remove(CSweetMemoryCapabilities.Query);
        var grant = await db.AgentInstallationGrants.SingleAsync();
        grant.RequiredCapabilitiesJson = JsonSerializer.Serialize(session.Grant.RequestedCapabilities);
        if (consumer == "claim")
        {
            var current = await db.AgentWorkItems.SingleAsync();
            current.Kind = AgentWorkKind.Event; current.SourceId = Guid.NewGuid().ToString("D");
            var item = await db.CoreWorkTasks.SingleAsync(x => x.Id == setup.Item);
            item.ClaimEventId = Guid.Parse(current.SourceId); item.AssignedEmployeeId = f.EmployeeId;
            item.AssignedAgentInstallationId = f.InstallationId;
        }
        await db.SaveChangesAsync();
        return (setup, published?.CommentId ?? Guid.Empty, session, work);
    }

    [MemoryPostgresTheory]
    [InlineData("coordination")]
    [InlineData("stage")]
    [InlineData("claim")]
    public async Task InstructionProviderInjectsOnlyCurrentPublishedCaseTextAndTracksItsProvenance(string consumer)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f, consumer);
        var provider = new InstructionDispatchProvider();
        var result = await RunInstructionModelAsync(f, s.Session, s.Work, provider);
        Assert.All(result, x => Assert.True(x.Succeeded, x.Error)); Assert.Equal(1, provider.Sends);
        Assert.Contains(SharedInstruction, provider.Context); Assert.DoesNotContain(PrivateInstructionContext, provider.Context);
        Assert.DoesNotContain("Unrelated personal information", provider.Context);
        Assert.DoesNotContain(s.Setup.Selection.ConversationId.ToString("D"), provider.Context);
        Assert.Contains(s.Setup.Item.ToString("D"), provider.Context);
        await using var db = f.Context();
        var receipt = Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Equal(PlatformCapabilities.LlmChatStream, receipt.Capability); Assert.Equal(s.Work.Id, receipt.WorkId);
        Assert.NotNull((await db.AgentRunLogs.SingleAsync()).ProviderStartedAt);
        Assert.True((await db.AgentRunLogs.SingleAsync()).PromptMemoryCharacters > 0);
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        // A second model call uses the same current instruction without accumulating duplicate receipts.
        Assert.All(await RunInstructionModelAsync(f, s.Session, s.Work, new InstructionDispatchProvider()), x => Assert.True(x.Succeeded, x.Error));
        Assert.Single(await db.AgentMemoryReadReceipts.AsNoTracking().ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("edit")]
    [InlineData("withdraw")]
    [InlineData("partner-grant")]
    [InlineData("completed")]
    public async Task InstructionProviderDeniesChangesWhileWaitingWithoutResettingUnreadRuntime(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f);
        var provider = new InstructionDispatchProvider { BeforeClient = async () =>
        {
            await using var changed = f.Context();
            if (change == "edit") await InstructionComments(changed).UpdateCommentAsync(f.OrganizationId, s.Setup.Board, s.Setup.Item,
                s.Comment, s.Setup.User, new("Make the paddle green.", 1, "waiting-edit"));
            if (change == "withdraw") await InstructionComments(changed).DeleteCommentAsync(f.OrganizationId, s.Setup.Board, s.Setup.Item,
                s.Comment, s.Setup.User, new(1, "waiting-withdraw"));
            if (change == "partner-grant")
            {
                var partner = (await changed.AgentCoordinationSessions.SingleAsync()).TargetInstallationId;
                await changed.ScopedActionGrants.Where(x => x.SubjectId == partner && x.Action == WorkItemActions.ReadComments).ExecuteDeleteAsync();
            }
            if (change == "completed") await changed.AgentWorkItems.Where(x => x.Id == s.Work.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, AgentWorkStatus.Completed));
        }};
        var failure = Assert.Single(await RunInstructionModelAsync(f, s.Session, s.Work, provider));
        Assert.False(failure.Succeeded); Assert.True(failure.FailureCode == "llm.dispatch_denied", provider.Failure?.ToString());
        Assert.Equal(0, provider.Sends); Assert.Null(provider.Context);
        await using var db = f.Context();
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.Null((await db.AgentRunLogs.SingleAsync()).ProviderStartedAt);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstructionProviderRejectsLateAnswerAndRechecksTransportRetry(bool retry)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f);
        async Task Change()
        {
            await using var db = f.Context();
            await InstructionComments(db).DeleteCommentAsync(f.OrganizationId, s.Setup.Board, s.Setup.Item,
                s.Comment, s.Setup.User, new(1, "late-withdraw"));
        }
        var provider = new InstructionDispatchProvider { BeforeResponse = retry ? null : Change, BeforeRetry = retry ? Change : null };
        var failure = Assert.Single(await RunInstructionModelAsync(f, s.Session, s.Work, provider));
        Assert.False(failure.Succeeded); Assert.True(failure.FailureCode == "llm.dispatch_denied", provider.Failure?.ToString());
        Assert.Equal(1, provider.Sends); Assert.DoesNotContain("Completed", failure.Payload.ToStringUtf8());
        await using var db = f.Context();
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.NotNull((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.DoesNotContain(await db.AuditEvents.ToArrayAsync(), x => x.EventType == "model.response.chunk");
    }

    [MemoryPostgresTheory]
    [InlineData("unbound")]
    [InlineData("unattributed")]
    public async Task InstructionProviderDoesNotUseCallerCaseHintsOrUnattributedWork(string scenario)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f, scenario == "unbound" ? "unbound" : "coordination");
        var provider = new InstructionDispatchProvider();
        Assert.All(await RunInstructionModelAsync(f, s.Session, s.Work, provider, scenario != "unattributed"), x => Assert.True(x.Succeeded, x.Error));
        Assert.DoesNotContain(SharedInstruction, provider.Context);
        await using var db = f.Context(); Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstructionProviderFreezesAbsenceOfFirstPublicationWhileWaiting(bool publishWhileWaiting)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f, publish: false);
        var provider = new InstructionDispatchProvider();
        if (publishWhileWaiting) provider.BeforeClient = async () => { await PublishInstructionAsync(f, s.Setup); };
        var results = await RunInstructionModelAsync(f, s.Session, s.Work, provider);
        if (publishWhileWaiting)
        {
            Assert.Equal("llm.dispatch_denied", Assert.Single(results).FailureCode); Assert.Equal(0, provider.Sends);
        }
        else
        {
            Assert.All(results, x => Assert.True(x.Succeeded, x.Error)); Assert.Equal(1, provider.Sends);
            Assert.DoesNotContain(SharedInstruction, provider.Context);
        }
        await using var db = f.Context(); Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task InstructionProviderNeverRecreatesForgottenMemoryFromItsCanonicalComment()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f);
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), s.Setup.Item.ToString("D"), "csweet");
        await f.Store.DeleteScopeAsync(ns.Partition);
        var provider = new InstructionDispatchProvider();
        Assert.All(await RunInstructionModelAsync(f, s.Session, s.Work, provider), x => Assert.True(x.Succeeded, x.Error));
        Assert.Equal(1, provider.Sends); Assert.DoesNotContain(SharedInstruction, provider.Context);
        Assert.Empty((await f.Store.ExportAsync(ns.Partition)).Episodes);
        await using var db = f.Context(); Assert.True(await db.WorkItemComments.AnyAsync(x => x.Id == s.Comment && x.DeletedAt == null));
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("count")]
    [InlineData("characters")]
    public async Task InstructionProviderRejectsOversizedContextWithoutSilentlyDroppingInstructions(string bound)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f);
        var count = bound == "count" ? 17 : 5;
        var comments = new List<Guid> { s.Comment };
        for (var n = 1; n < count; n++) comments.Add((await PublishInstructionAsync(f, s.Setup)).CommentId);
        if (bound == "characters")
        {
            await using var db = f.Context();
            foreach (var comment in comments) await InstructionComments(db).UpdateCommentAsync(f.OrganizationId, s.Setup.Board, s.Setup.Item,
                comment, s.Setup.User, new(new string('a', 8192), 1, comment.ToString("D")));
        }
        var provider = new InstructionDispatchProvider();
        var failure = Assert.Single(await RunInstructionModelAsync(f, s.Session, s.Work, provider));
        Assert.False(failure.Succeeded); Assert.Equal("llm.dispatch_denied", failure.FailureCode); Assert.Equal(0, provider.Sends);
        await using var verify = f.Context(); Assert.Empty(await verify.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Null((await verify.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }

    private sealed class InstructionDispatchWire(Func<Task> change) : HttpMessageHandler
    {
        internal int Calls;
        internal string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token); await change();
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{\"error\":{\"message\":\"retry fixture\",\"type\":\"server_error\"}}", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("Retry-After", "0"); return response;
        }
    }

    [MemoryPostgresTheory]
    [InlineData("withdraw")]
    [InlineData("partner-grant")]
    public async Task InstructionProviderRechecksRealHttpRetryWithoutCommittingPendingTelemetry(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f);
        using var wire = new InstructionDispatchWire(async () =>
        {
            await using var db = f.Context();
            if (change == "withdraw") await InstructionComments(db).DeleteCommentAsync(f.OrganizationId, s.Setup.Board, s.Setup.Item,
                s.Comment, s.Setup.User, new(1, "http-withdraw"));
            else
            {
                var partner = (await db.AgentCoordinationSessions.SingleAsync()).TargetInstallationId;
                await db.ScopedActionGrants.Where(x => x.SubjectId == partner && x.Action == WorkItemActions.ReadComments).ExecuteDeleteAsync();
            }
        });
        using var http = new HttpClient(wire);
        await using var providerDb = f.Context();
        var factory = new OpenAiCompatibleLlmProviderFactory(providerDb, new InMemoryLlmProviderSecretStore(),
            NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance) { TransportOverride = new HttpClientPipelineTransport(http) };
        var failure = Assert.Single(await RunInstructionModelAsync(f, s.Session, s.Work, factory));
        Assert.Equal("llm.dispatch_denied", failure.FailureCode); Assert.Equal(1, wire.Calls);
        Assert.Contains(SharedInstruction, wire.Body); Assert.DoesNotContain(PrivateInstructionContext, wire.Body);
        await using var verify = f.Context();
        Assert.NotNull((await verify.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.NotNull((await verify.AgentRunLogs.SingleAsync()).ProviderStartedAt);
        Assert.DoesNotContain(await verify.AuditEvents.ToArrayAsync(), x => x.EventType == "model.response.chunk");
    }

    [MemoryPostgresFact]
    public async Task InstructionProviderRetainsErasureLineageAndRequiresNativeWorkReviewWithoutDiagnosticCopies()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var s = await PrepareInstructionDispatchAsync(f);
        var provider = new InstructionDispatchProvider { BeforeClient = async () =>
        {
            await using var pending = f.Context(); Assert.Empty(await pending.AgentMemoryReadReceipts.ToArrayAsync());
            var id = await pending.AgentRunLogs.Select(x => x.Id).SingleAsync();
            var copy = await pending.AuditOutbox.SingleAsync(x => x.SourceEntityId == id);
            var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(copy.RequestJson)!;
            using var body = JsonDocument.Parse(request.Payload!.Value);
            var policy = Assert.Single(body.RootElement.GetProperty("evidence").EnumerateObject());
            Assert.Equal("contentPolicy", policy.Name); Assert.Equal("memory-content-omitted-v1", policy.Value.GetString());
        } };
        Assert.All(await RunInstructionModelAsync(f, s.Session, s.Work, provider), x => Assert.True(x.Succeeded, x.Error));
        await using var db = f.Context();
        var log = await db.AgentRunLogs.SingleAsync(); Assert.Null(log.OutputPreview);
        var copies = await db.AuditOutbox.Where(x => x.SourceEntityId == log.Id).ToArrayAsync(); Assert.NotEmpty(copies);
        var sawOmission = false;
        foreach (var copy in copies)
        {
            var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(copy.RequestJson)!;
            if (request.Payload is not { } payload) continue;
            var text = Encoding.UTF8.GetString(payload.Span);
            Assert.DoesNotContain(SharedInstruction, text); Assert.DoesNotContain(PrivateInstructionContext, text);
            Assert.DoesNotContain("Completed the requested paddle change", text);
            sawOmission |= text.Contains("memory-content-omitted-v1", StringComparison.Ordinal);
        }
        Assert.True(sawOmission); db.ChangeTracker.Clear();
        var id = WorkInstructionMemorySource.EpisodeId(s.Comment, 1);
        var review = ErasureService(f, db);
        var impact = await review.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, id, s.Setup.User);
        // Source lineage identifies the affected runtime, but is not consent to erase a native work payload.
        // The existing full-work audience review stays fail-closed until that separate plan item is implemented.
        Assert.Equal("memory_erasure_work_audience_review_required", impact.ApplyBlockedReason); Assert.Null(impact.EvidenceToken);
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), s.Setup.Item.ToString("D"), "csweet");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var inventory = await new MemoryErasureReadInventory(db).ReadAsync(f.OrganizationId,
                [new(MemoryErasureKind.Episode, id, ns.Partition)], default);
            Assert.Equal(Guid.Parse(s.Session.RuntimeInstanceId), Assert.Single(inventory.RuntimeIds));
            var delivered = Assert.Single(inventory.Delivered); Assert.Equal(s.Work.Id, delivered.WorkId);
            Assert.Contains(delivered.Read.References, x => x.Id == id && x.Partition == ns.Partition);
        }
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.Null((await db.AgentWorkItems.SingleAsync()).MemoryErasedAt);
        Assert.NotNull(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, id));
    }
}
