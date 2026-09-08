using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.GenAi;
using CSweet.Application.Setup;
using CSweet.Contracts.GenAi;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CSweet.Infrastructure.Persistence;

namespace CSweet.UnitTests;

public sealed class ConnectorMediaTransferTests
{
    [Fact]
    public async Task WrongMediaResponseOwnerIsWithheldBeforeSecretExtraction()
    {
        await using var f = await Fixture.Create(secret: true, responseBinding: true);
        f.Provider.IncludeSecret = true; f.Provider.ResultOwner = "another-account";
        await f.Approve();
        for (var i = 0; i < 5 && f.Plan.Status != "Indeterminate"; i++) await f.Dispatch();
        Assert.Equal("Indeterminate", f.Plan.Status);
        Assert.Null(f.Plan.ResultJson);
        Assert.DoesNotContain("provider-secret", f.Provider.Secrets.Values);
    }
    [Fact]
    public async Task ChangedStoredBytesWithUnchangedMetadataAreNeverSent()
    {
        await using var f = await Fixture.Create(); await f.Approve(); await f.Dispatch(); await f.Dispatch();
        f.Provider.Bytes[0] ^= 1;
        await f.Dispatch();
        Assert.Empty(f.Provider.Chunks);
        Assert.Equal("Blocked", f.Plan.Status);
        Assert.Null(f.Plan.ResultJson);
    }

    [Fact]
    public async Task MissingIngestionProofsBlockBeforeSessionCreation()
    {
        await using var f = await Fixture.Create(); await f.Approve();
        f.Inner.Db.MediaAssetChunks.RemoveRange(f.Inner.Db.MediaAssetChunks);
        await f.Inner.Db.SaveChangesAsync();
        await f.Dispatch();
        Assert.Empty(f.Provider.Steps);
        Assert.NotEqual("Completed", f.Plan.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedSourceAccessStopsTransferBeforeTheNextProviderExchange(bool sessionStarted)
    {
        await using var f = await Fixture.Create();
        await f.Approve();
        await f.Dispatch();
        if (sessionStarted) await f.Dispatch();
        var count = f.Provider.Steps.Count;
        var conversation = await f.Inner.Db.CoreConversations.Include(x => x.Participants).SingleAsync();
        conversation.Participants.Single().LeftAt = DateTimeOffset.UtcNow;
        await f.Inner.Db.SaveChangesAsync();
        await f.Dispatch();
        Assert.Equal(count, f.Provider.Steps.Count);
        Assert.Empty(f.Provider.Chunks);
        Assert.NotEqual("Completed", f.Plan.Status);
        Assert.Null(f.Plan.ResultJson);
    }

    [Fact]
    public async Task MatchingChangedAttachmentAndAssetStillCannotAlterAnApprovedPlan()
    {
        await using var f = await Fixture.Create();
        var attachment = await f.Inner.Db.ConversationMessageAttachments.SingleAsync();
        f.Provider.Asset.Sha256 = attachment.Sha256 = new string('b', 64);
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Inner.Service.RevalidateAsync(
            f.Inner.Organization, f.Inner.Requester.Id, f.Plan.Id, f.Plan.PlanHash, default));
        Assert.Empty(f.Provider.Steps);
    }

    [Fact]
    public void QueueSchemaAndPostgresExclusionQueryGenerateExpectedSqlWithoutOpeningADatabase()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var query = db.ConnectorExecutions.Where(x => x.Status == "Executing" &&
            !db.PluginOperationalStates.Any(job => job.Kind == ConnectorMediaTransferService.ActiveKind && job.ExternalKey == x.Id.ToString()));
        Assert.Contains("NOT EXISTS", query.ToQueryString());
        var script = db.GetService<IMigrator>().GenerateScript(
            toMigration: "20260908000100_AddPluginOperationalStateAvailability",
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("ADD \"AvailableAt\" timestamp with time zone", script);
        Assert.Contains("IX_PluginOperationalStates_Kind_AvailableAt", script);
    }

    [Fact]
    public async Task AWorkerLosingItsClaimCannotOverwriteTheNewOwnerOrResendItsChunk()
    {
        await using var f = await Fixture.Create(); await f.Approve(); await f.Dispatch(); await f.Dispatch();
        var options = (DbContextOptions<CSweetDbContext>)f.Inner.Db.GetService<IDbContextOptions>();
        f.Provider.OnChunk = async () =>
        {
            await using var competing = new CSweetDbContext(options);
            var execution = await competing.ConnectorExecutions.SingleAsync(); execution.Revision++;
            await competing.SaveChangesAsync();
        };
        await f.Dispatch(); Assert.Equal("Executing", f.Plan.Status); Assert.Null(f.Plan.ResultJson);
        f.Provider.OnChunk = null; f.Provider.ProbeCompleted = true; await f.MakeDue(); await f.Dispatch();
        Assert.Equal("Completed", f.Plan.Status); Assert.Single(f.Provider.Chunks);
    }

    [Fact]
    public async Task ApprovalDispatchUploadsOnceAndCommitsSanitizedCompletionBeforeReporting()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.StartAsync(f.Plan, default));
        Assert.Empty(f.Provider.Steps);
        await f.Approve(); await f.Dispatch(); await f.Dispatch(); await f.Dispatch();
        Assert.Equal(new[] { ConnectorMediaStep.Begin, ConnectorMediaStep.Probe, ConnectorMediaStep.Chunk }, f.Provider.Steps);
        Assert.Equal("Completed", f.Plan.Status); Assert.Equal("{\"data\":\"confirmed\"}", f.Plan.ResultJson);
        Assert.Equal(f.Provider.Bytes, Assert.Single(f.Provider.Chunks)); Assert.Empty(f.Provider.Secrets);
        Assert.False(await f.Dispatch());
        Assert.DoesNotContain(Fixture.Session, (await f.Inner.Db.PluginOperationalStates.ToArrayAsync()).Select(x => x.PayloadJson));
        Assert.DoesNotContain((await f.Inner.Db.PluginOperationalStates.ToArrayAsync()), x => x.PayloadJson.Contains(Fixture.Session));
    }

    [Fact]
    public async Task LostFinalResponseResumesByProbeWithoutSendingASecondChunk()
    {
        await using var f = await Fixture.Create(); await f.Approve();
        f.Provider.TimeoutChunk = true;
        await f.Dispatch(); await f.Dispatch(); await f.Dispatch();
        Assert.Equal("Executing", f.Plan.Status); Assert.Equal("Probe", f.Progress.Phase);
        Assert.False(await f.Dispatch()); // Durable backoff, not a retained wait or immediate retry.
        await f.MakeDue(); f.Provider.ProbeCompleted = true; await f.Dispatch();
        Assert.Equal("Completed", f.Plan.Status); Assert.Single(f.Provider.Chunks);
        Assert.Equal(1, f.Provider.Steps.Count(x => x == ConnectorMediaStep.Begin));
    }

    [Fact]
    public async Task WorkerShutdownDuringAChunkPersistsProbeRecoveryBeforePropagatingCancellation()
    {
        await using var f = await Fixture.Create(); await f.Approve(); await f.Dispatch(); await f.Dispatch();
        using var stopping = new CancellationTokenSource();
        f.Provider.OnChunk = () => { stopping.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ProcessNextAsync(stopping.Token));
        Assert.Equal("Executing", f.Plan.Status); Assert.Equal("Probe", f.Progress.Phase);
        f.Provider.OnChunk = null; f.Provider.ProbeCompleted = true; await f.MakeDue(); await f.Dispatch();
        Assert.Equal("Completed", f.Plan.Status); Assert.Single(f.Provider.Chunks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainInitiationNeverStartsAnotherUpload(bool vaultFailure)
    {
        await using var f = await Fixture.Create(); await f.Approve();
        f.Provider.TimeoutBegin = !vaultFailure; f.Provider.FailVaultWrite = vaultFailure;
        await f.Dispatch(); Assert.Equal("Indeterminate", f.Plan.Status);
        Assert.False(await f.Dispatch()); Assert.Equal(ConnectorMediaStep.Begin, Assert.Single(f.Provider.Steps));
        Assert.Null(f.Plan.ResultJson); Assert.Empty(f.Provider.Chunks);
    }

    [Fact]
    public async Task CrashAfterSessionSaveUsesTheExistingOpaqueSession()
    {
        await using var f = await Fixture.Create(); await f.Approve(); await f.Dispatch();
        var job = f.Job;
        job.PayloadJson = JsonSerializer.Serialize(f.Progress with { Phase = "Begin", InFlight = true }, Fixture.Json);
        await f.MakeDue(); await f.Dispatch(); await f.Dispatch();
        Assert.Equal("Completed", f.Plan.Status); Assert.Equal(1, f.Provider.Steps.Count(x => x == ConnectorMediaStep.Begin));
    }

    [Fact]
    public async Task RevocationDuringChunkPreventsCompletionAndFurtherTransfers()
    {
        await using var f = await Fixture.Create(); await f.Approve(); await f.Dispatch(); await f.Dispatch();
        f.Provider.OnChunk = async () => { f.Inner.Connection.Status = PluginConnectionStatus.Revoked; await f.Inner.Db.SaveChangesAsync(); };
        await f.Dispatch(); Assert.Equal("Indeterminate", f.Plan.Status); Assert.Null(f.Plan.ResultJson);
        Assert.False(await f.Dispatch()); Assert.Single(f.Provider.Chunks);
    }

    [Fact]
    public async Task ChangedAssetStopsBeforeTheNextChunk()
    {
        await using var f = await Fixture.Create(); await f.Approve(); await f.Dispatch(); await f.Dispatch();
        f.Provider.Asset.Sha256 = new string('e', 64); await f.Inner.Db.SaveChangesAsync();
        await f.Dispatch(); Assert.Equal("Indeterminate", f.Plan.Status); Assert.Empty(f.Provider.Chunks);
        Assert.False(await f.Dispatch());
    }

    [Fact]
    public async Task RepeatedNoProgressStopsVisiblyInsteadOfLoopingForever()
    {
        await using var f = await Fixture.Create(); await f.Approve();
        f.Provider.NeverProgress = true; await f.Dispatch(); await f.Dispatch();
        for (var attempt = 0; attempt < 8; attempt++) { await f.MakeDue(); await f.Dispatch(); }
        Assert.Equal("Indeterminate", f.Plan.Status); Assert.Null(f.Plan.ResultJson);
        Assert.False(await f.Dispatch()); Assert.Equal(1, f.Provider.Steps.Count(x => x == ConnectorMediaStep.Begin));
    }

    [Fact]
    public async Task MissingRequiredSecretFailsClosedRatherThanReturningProviderData()
    {
        await using var f = await Fixture.Create(secret: true); await f.Approve();
        await f.Dispatch(); await f.Dispatch(); await f.Dispatch();
        Assert.Equal("Indeterminate", f.Plan.Status); Assert.Null(f.Plan.ResultJson);
    }

    [Fact]
    public async Task SecretFieldsAreVaultedBeforeCompletionAndSessionCleanupRetriesDurably()
    {
        await using var f = await Fixture.Create(secret: true); await f.Approve();
        f.Provider.IncludeSecret = true; f.Provider.FailRemoveOnce = true;
        await f.Dispatch(); await f.Dispatch(); await f.Dispatch();
        Assert.Equal("Completed", f.Plan.Status); Assert.DoesNotContain("provider-secret", f.Plan.ResultJson);
        Assert.Contains("plugin-secret:", f.Plan.ResultJson); Assert.NotNull(f.Job.AvailableAt);
        await f.Dispatch(); Assert.Null(f.Job.AvailableAt);
        Assert.DoesNotContain(f.Provider.Secrets.Keys, x => x.StartsWith("response.media-session."));
        Assert.Contains("provider-secret", f.Provider.Secrets.Values);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public const string Session = "https://api.example.com/items?upload_id=opaque-secret";
        public ConnectorPlanServiceTests.Fixture Inner { get; private init; } = null!;
        public Provider Provider { get; private init; } = null!;
        public ConnectorExecution Plan { get; private set; } = null!;
        private OrganizationUser Manager { get; set; } = null!;
        public ConnectorActionApprovalService Approval => new(Inner.Db, Inner.Service, Provider);
        public ConnectorMediaTransferService Service => new(Inner.Db, Approval, Provider, Provider, Provider, Provider, Provider);
        public PluginOperationalState Job => Inner.Db.PluginOperationalStates.Local.Single(x => x.Kind is ConnectorMediaTransferService.ActiveKind or ConnectorMediaTransferService.FinishedKind);
        public ConnectorMediaTransferService.Progress Progress => JsonSerializer.Deserialize<ConnectorMediaTransferService.Progress>(Job.PayloadJson, Json)!;
        public Task<bool> Dispatch() => new ConnectorActionDispatchService(Inner.Db,
            new ConnectorMutationExecutor(Inner.Db, Approval, Provider, Provider, Provider), Approval, Service).ProcessNextAsync(default);
        public async Task MakeDue() { Job.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1); await Inner.Db.SaveChangesAsync(); }
        public async Task Approve()
        {
            var proposal = await Approval.RequestAsync(Inner.Organization, Inner.Requester.Id, Plan.Id, Plan.PlanHash, default);
            var binding = ConnectorActionApprovalService.Parse(proposal);
            await Approval.DecideAsync(Inner.Organization, Manager.Id, new(proposal.Id, "Approve", null,
                binding.PayloadHash, binding.ExpectedRevision, binding.IdempotencyKey, "decision", binding.ResourceId), default);
        }
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public static async Task<Fixture> Create(bool secret = false, bool responseBinding = false)
        {
            var inner = await ConnectorPlanServiceTests.Fixture.Create(responseBinding: responseBinding);
            var provider = new Provider();
            provider.Asset.OrganizationId = inner.Organization; inner.Db.MediaAssets.Add(provider.Asset);
            inner.Db.MediaAssetChunks.Add(new() { MediaAssetId = provider.Asset.Id, Offset = 0,
                Length = provider.Bytes.Length, Sha256 = provider.Asset.Sha256, AssetSha256 = provider.Asset.Sha256 });
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new
                { search = new { type = "string" }, asset = new { type = "string", format = "uuid" } }, required = new[] { "search", "asset" }, additionalProperties = false });
            var manifest = JsonSerializer.Deserialize<PluginManifest>(inner.Connector.PackageVersion!.ManifestJson, Json)!;
            var output = secret ? JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"data":{"type":"string"},"secret":{"type":"object","properties":{"secretReference":{"type":"string"}},"required":["secretReference"],"additionalProperties":false}},"required":["data","secret"],"additionalProperties":false}""") : manifest.Provides[0].OutputSchema;
            manifest = manifest with { Provides = [manifest.Provides[0] with { InputSchema = schema, OutputSchema = output, Idempotency = "caller-key" }],
                ProviderOperations = [manifest.ProviderOperations[0] with { InputSchema = schema, OutputSchema = output, Idempotency = "caller-key", Effect = "write",
                    Http = manifest.ProviderOperations[0].Http! with { Method = "POST", MediaInput = "/asset",
                        MediaProtocol = ConnectorResumableProtocol.Name, SecretResponseFields = secret ? ["/secret"] : [] } }] };
            inner.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, Json);
            var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = inner.Organization, DisplayName = "Manager", EmployeeType = EmployeeType.Human };
            inner.Db.CoreOrganizationUsers.AddRange(manager, new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = inner.Organization,
                DisplayName = "Specialist", EmployeeType = EmployeeType.Agent, AgentInstallationId = inner.Requester.Id, ReportsToOrganizationUserId = manager.Id });
            await inner.Db.SaveChangesAsync();
            var f = new Fixture { Inner = inner, Provider = provider, Manager = manager };
            var source = await ConnectorMediaSourceTests.AttachAsync(inner.Db, inner.Organization, inner.Requester, provider.Asset);
            f.Plan = await inner.Service.PrepareAsync(inner.Organization, inner.Requester.Id, ConnectorPlanServiceTests.Fixture.Capability,
                JsonSerializer.SerializeToElement(new { search = "approved title", asset = provider.Asset.Id }), "upload-once", default, source);
            return f;
        }
    }

    private sealed class Provider : IConnectorMediaTransport, IConnectorHttpTransport, IMediaAssetService, IPluginSecretStore, IAuditEventWriter
    {
        public byte[] Bytes { get; } = [1, 2, 3, 4, 5];
        public MediaAsset Asset { get; } = new() { Id = Guid.NewGuid(), FileName = "video.mp4", ContentType = "video/mp4", SizeBytes = 5,
            Sha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4, 5 })).ToLowerInvariant() };
        public List<ConnectorMediaStep> Steps { get; } = [];
        public List<byte[]> Chunks { get; } = [];
        public Dictionary<string, string> Secrets { get; } = [];
        public bool TimeoutBegin, TimeoutChunk, FailVaultWrite, ProbeCompleted, NeverProgress, IncludeSecret, FailRemoveOnce;
        public string ResultOwner = "confirmed";
        public Func<Task>? OnChunk;
        public async Task<ConnectorMediaResponse> SendAsync(FrozenConnectorPlan plan, ConnectorMediaStep step, string? session,
            long offset, ReadOnlyMemory<byte> chunk, long maximumSentBytes, Func<CancellationToken, Task> revalidate, CancellationToken ct)
        {
            await revalidate(ct); Steps.Add(step);
            if (step == ConnectorMediaStep.Begin)
            { if (TimeoutBegin) throw new HttpRequestException(); return new(200, [], Fixture.Session, null, null); }
            Assert.Equal(Fixture.Session, session);
            if (step == ConnectorMediaStep.Probe && !ProbeCompleted) return new(308, [], null, 0, null);
            if (step == ConnectorMediaStep.Chunk)
            {
                Chunks.Add(chunk.ToArray()); if (OnChunk is not null) await OnChunk();
                if (TimeoutChunk) throw new HttpRequestException();
                if (NeverProgress) return new(308, [], null, 0, null);
            }
            return new(201, JsonSerializer.SerializeToUtf8Bytes(IncludeSecret ? new { data = ResultOwner, secret = "provider-secret" } : (object)new { data = ResultOwner }), null, null, null);
        }
        public Task<ConnectorProviderResponse> SendAsync(Guid connector, Guid connection, ConnectorPreparedRequest request,
            Func<CancellationToken, Task> revalidate, CancellationToken ct) => throw new InvalidOperationException("No unapproved direct requests.");
        public Task<(MediaAssetResponse Asset, Stream Content)?> OpenReadAsync(Guid id, Guid org, CancellationToken ct = default)
        {
            Assert.Equal(Asset.Id, id); Assert.Equal(Asset.OrganizationId, org);
            return Task.FromResult<(MediaAssetResponse, Stream)?>(
                (new(Asset.Id, Asset.FileName, Asset.ContentType, Asset.SizeBytes, Asset.Sha256, null, null, null, Asset.CreatedAt), new MemoryStream(Bytes, false)));
        }
        public Task SetAsync(Guid installation, string key, string value, CancellationToken ct = default)
        { if (FailVaultWrite) throw new IOException(); Secrets[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(Guid installation, string key, CancellationToken ct = default) => Task.FromResult(Secrets.GetValueOrDefault(key));
        public Task RemoveAsync(Guid installation, string key, CancellationToken ct = default)
        { if (FailRemoveOnce) { FailRemoveOnce = false; throw new IOException(); } Secrets.Remove(key); return Task.CompletedTask; }
        public Task WriteAsync(string type, string entity, Guid? id, string? summary, string? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MediaAssetResponse> SaveUploadAsync(Guid org, string name, string type, Stream content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaAssetResponse?> GetAsync(Guid id, Guid org, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, Guid org, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
