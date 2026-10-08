using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"memory-broker-{Guid.NewGuid():N}.db");
    private readonly CSweetDbContext _db = new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private SqliteMemoryStore _store = null!;
    private readonly Guid _organization = Guid.NewGuid();
    private readonly Guid _employee = Guid.NewGuid();
    private readonly Guid _installation = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _store = new SqliteMemoryStore(_path);
        await _store.InitializeAsync();
        _db.AgentInstallations.Add(new AgentInstallation
        {
            Id = _installation, BusinessId = _organization.ToString("D"), IsEnabled = true
        });
        _db.CoreOrganizationUsers.AddRange(
            new OrganizationUser { Id = _employee, OrganizationId = _organization,
                EmployeeType = EmployeeType.Agent, AgentInstallationId = _installation },
            new OrganizationUser { Id = _user, OrganizationId = _organization, EmployeeType = EmployeeType.Human });
        _db.CoreConversations.Add(new Conversation { Id = Guid.NewGuid(), OrganizationId = _organization,
            AgentOrganizationUserId = _employee, InitiatedByOrganizationUserId = _user });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _store.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }

    [Theory]
    [InlineData("shifted-field")]
    [InlineData("separator")]
    [InlineData("extra-field")]
    [InlineData("foreign-application")]
    [InlineData("unknown-scope")]
    public async Task SearchRejectsNoncanonicalPartitions(string scenario)
    {
        var victim = Guid.NewGuid().ToString("D");
        var real = EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), victim, "csweet").Partition;
        var supplied = scenario switch
        {
            "shifted-field" => real with { AgentId = null, UserId = victim },
            "separator" => real with { AgentId = null, ApplicationId = $"csweet/{victim}" },
            "extra-field" => EmployeePartition() with { UserId = _user.ToString("D") },
            "foreign-application" => EmployeePartition() with { ApplicationId = Guid.NewGuid().ToString("D") },
            _ => EmployeePartition() with { CustomNamespace = "unknown" }
        };
        if (scenario is "shifted-field" or "separator") Assert.Equal(real.Key, supplied.Key);
        await AppendAsync(real, "secret memory");

        var response = await SearchAsync(supplied);

        Assert.False(response.Succeeded);
        Assert.DoesNotContain("secret memory", response.Payload.ToStringUtf8());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnCanonicalAndLegacyInstallationNamespacesRemainDistinct(bool legacy)
    {
        var partition = EmployeePartition(legacy ? _installation.ToString("D") : "csweet");
        await AppendAsync(partition, "allowed memory");
        await AppendAsync(EmployeePartition(legacy ? "csweet" : _installation.ToString("D")), "other memory");

        var response = await SearchAsync(partition);

        Assert.True(response.Succeeded, response.Error);
        var items = JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!;
        Assert.Equal("allowed memory", Assert.Single(items).Content);
    }

    [Fact]
    public async Task MissingResolverCannotTrustSessionMemoryIdentity()
    {
        var session = Session();
        session.MemoryTenantId = _organization.ToString("D");
        session.MemoryEmployeeId = _employee.ToString("D");
        var handler = new PlatformMemoryCapabilityHandler(_store, NullLogger<PlatformMemoryCapabilityHandler>.Instance);

        var response = await handler.HandleAsync(session, Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(EmployeePartition(), MemoryScope.Agent, "memory")), default);

        Assert.False(response.Succeeded);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("archived")]
    [InlineData("disabled")]
    [InlineData("foreign-business")]
    public async Task IdentityIsRevalidatedForEveryRequest(string scenario)
    {
        var session = Session();
        var handler = Handler();
        var request = Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(EmployeePartition(), MemoryScope.Agent, "memory"));
        Assert.True((await handler.HandleAsync(session, request, default)).Succeeded);
        var employee = await _db.CoreOrganizationUsers.SingleAsync(x => x.Id == _employee);
        if (scenario == "inactive") employee.IsActive = false;
        if (scenario == "archived") employee.ArchivedAt = DateTimeOffset.UtcNow;
        if (scenario == "disabled") (await _db.AgentInstallations.SingleAsync()).IsEnabled = false;
        if (scenario == "foreign-business") employee.OrganizationId = Guid.NewGuid();
        await _db.SaveChangesAsync();

        Assert.False((await handler.HandleAsync(session, request, default)).Succeeded);
    }

    [Fact]
    public async Task RelationshipRequiresCurrentCounterpartyAndEstablishedConversation()
    {
        var partition = EmployeeMemoryNamespaces.UserRelationship(_organization.ToString("D"),
            _employee.ToString("D"), _user.ToString("D"), "csweet").Partition;
        Assert.True((await SearchAsync(partition)).Succeeded);
        var stranger = Guid.NewGuid();
        _db.CoreOrganizationUsers.Add(new OrganizationUser { Id = stranger, OrganizationId = _organization });
        await _db.SaveChangesAsync();
        Assert.False((await SearchAsync(EmployeeMemoryNamespaces.UserRelationship(_organization.ToString("D"),
            _employee.ToString("D"), stranger.ToString("D"), "csweet").Partition)).Succeeded);
        (await _db.CoreOrganizationUsers.SingleAsync(x => x.Id == _user)).IsActive = false;
        await _db.SaveChangesAsync();
        Assert.False((await SearchAsync(partition)).Succeeded);
    }

    [Theory]
    [InlineData("team")]
    [InlineData("role")]
    public async Task SharedNamespaceRequiresCurrentMembership(string kind)
    {
        var id = Guid.NewGuid();
        var partition = kind == "team"
            ? EmployeeMemoryNamespaces.Team(_organization.ToString("D"), id.ToString("D"), "csweet").Partition
            : EmployeeMemoryNamespaces.Role(_organization.ToString("D"), id.ToString("D"), "csweet").Partition;
        Assert.False((await SearchAsync(partition)).Succeeded);
        if (kind == "team")
        {
            _db.OrganizationTeams.Add(new OrganizationTeam { Id = id, OrganizationId = _organization });
            _db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = _organization,
                OrganizationUserId = _employee, TeamId = id, JoinedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        }
        else
        {
            _db.CoreRoles.Add(new Role { Id = id, OrganizationId = _organization });
            (await _db.CoreOrganizationUsers.SingleAsync(x => x.Id == _employee)).RoleId = id;
        }
        await _db.SaveChangesAsync();
        Assert.True((await SearchAsync(partition)).Succeeded);
        if (kind == "team") (await _db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        else (await _db.CoreOrganizationUsers.SingleAsync(x => x.Id == _employee)).RoleId = null;
        await _db.SaveChangesAsync();
        Assert.False((await SearchAsync(partition)).Succeeded);
    }

    [Fact]
    public async Task ManageGrantDoesNotAuthorizeOrganizationDeletion()
    {
        var partition = EmployeeMemoryNamespaces.Organization(_organization.ToString("D"), "csweet").Partition;
        await AppendAsync(partition, "business memory");

        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Manage,
            "delete-scope", partition), default);

        Assert.False(response.Succeeded);
        Assert.Single((await _store.ExportAsync(partition)).Episodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManageGrantCannotDeleteOwnedScopeOrHeldEvidence(bool held)
    {
        var partition = EmployeePartition();
        var episode = await AppendAsync(partition, "evidence");
        var heldEpisode = episode with { Id = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid().ToString(), LegalHold = held };
        await _store.AppendEpisodeAsync(heldEpisode);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Manage,
            "delete-scope", partition), default);
        Assert.False(response.Succeeded);
        Assert.Equal(2, (await _store.ExportAsync(partition)).Episodes.Count);
        Assert.Equal(held, (await _store.GetEpisodeAsync(partition, heldEpisode.Id))!.LegalHold);
    }

    [Fact]
    public async Task BrokerSearchFiltersSensitiveAndRejectedResultsEvenWhenPendingIsRequested()
    {
        var partition = EmployeePartition();
        var episode = await AppendAsync(partition, "memory evidence");
        await AppendAsync(partition, "restricted memory", MemorySensitivity.Restricted);
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "learned:topic", "memory", [], null, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Internal };
        await _store.UpsertEntityAsync(entity);
        foreach (var state in new[] { MemoryConfirmationState.Pending, MemoryConfirmationState.Rejected })
            await _store.WriteClaimAsync(new MemoryClaim(Guid.NewGuid(), partition, episode.Id, entity.Id, "memory",
                null, $"{state} secret", MemoryTrustTier.AgentInference, state, MemorySensitivity.Internal,
                1, 1, DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow));

        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(partition, MemoryScope.Agent, "memory", IncludePending: true)), default);

        Assert.True(response.Succeeded, response.Error);
        var items = JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!;
        Assert.Equal(episode.Id, Assert.Single(items).Id);
    }

    [Theory]
    [InlineData("get-claim")]
    [InlineData("list-claims")]
    public async Task ClaimReadsUseTheSameEligibilityRules(string operation)
    {
        var partition = EmployeePartition();
        var episode = await AppendAsync(partition, "memory evidence");
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "learned:topic", "memory", [], null, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Internal };
        await _store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), partition, episode.Id, entity.Id, "memory", null,
            "restricted claim", MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed,
            MemorySensitivity.Restricted, 1, 1, DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow);
        await _store.WriteClaimAsync(claim);
        var request = operation == "get-claim"
            ? Request(CSweetMemoryCapabilities.Query, operation, new { claimId = claim.Id })
            : Request(CSweetMemoryCapabilities.Query, operation, partition);

        var response = await Handler().HandleAsync(Session(), request, default);

        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(operation == "get-claim" ? "null" : "[]", response.Payload.ToStringUtf8());
    }

    [Theory]
    [InlineData("list-claims", false)]
    [InlineData("export", true)]
    public async Task RawReadOperationsAlsoRejectPartitionAliases(string operation, bool export)
    {
        var victim = Guid.NewGuid().ToString("D");
        var partition = EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), victim, "csweet").Partition;
        var alias = partition with { AgentId = null, UserId = victim };
        await AppendAsync(partition, "private memory");

        var response = await Handler().HandleAsync(Session(), Request(export ? CSweetMemoryCapabilities.Export
            : CSweetMemoryCapabilities.Query, operation, alias), default);

        Assert.False(response.Succeeded);
        Assert.True(response.Payload.IsEmpty);
    }

    [Fact]
    public async Task CapabilityGrantRemainsRequired()
    {
        var session = Session() with { Grant = new AuthorizedAgentGrant(new HashSet<string>(),
            new HashSet<string>(), new HashSet<string>(), 1) };
        var response = await Handler().HandleAsync(session, Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(EmployeePartition(), MemoryScope.Agent, "memory")), default);
        Assert.False(response.Succeeded);
    }

    [Theory]
    [InlineData("get-claim")]
    [InlineData("list-claims")]
    [InlineData("search")]
    public async Task UnderclassifiedClaimsCannotBypassSourceSensitivity(string operation)
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "restricted memory source", MemorySensitivity.Restricted);
        var entity = await EntityAsync(partition);
        var claim = Claim(source, entity);
        await _store.WriteClaimAsync(claim);
        var request = operation switch
        {
            "get-claim" => Request(CSweetMemoryCapabilities.Query, operation, new { claimId = claim.Id }),
            "search" => Request(CSweetMemoryCapabilities.Query, operation, new MemorySearchRequest(partition, MemoryScope.Agent, "memory")),
            _ => Request(CSweetMemoryCapabilities.Query, operation, partition)
        };
        var response = await Handler().HandleAsync(Session(), request, default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(operation == "get-claim" ? "null" : "[]", response.Payload.ToStringUtf8());
    }

    [Theory]
    [InlineData("find-entity")]
    [InlineData("find-entity-by-application-key")]
    public async Task RawEntityReadsWithholdUnclassifiedLegacyContent(string operation)
    {
        var partition = EmployeePartition();
        var entity = await EntityAsync(partition, MemorySensitivity.Restricted);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, operation,
            new { partition, canonicalName = entity.CanonicalName, applicationKey = entity.ApplicationKey }), default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal("null", response.Payload.ToStringUtf8());
    }

    [Fact]
    public async Task ExportFiltersAllLayersAndTheirReferences()
    {
        var partition = EmployeePartition();
        var allowed = await AppendAsync(partition, "allowed memory", MemorySensitivity.Personal);
        var secret = await AppendAsync(partition, "restricted source", MemorySensitivity.Restricted);
        var entity = await EntityAsync(partition);
        await EntityAsync(partition, MemorySensitivity.Restricted);
        var allowedClaim = Claim(allowed, entity);
        await _store.WriteClaimAsync(allowedClaim);
        await _store.WriteClaimAsync(Claim(secret, entity));
        await _store.WriteClaimAsync(Claim(allowed, entity) with { Confirmation = MemoryConfirmationState.Rejected });
        await _store.WriteBlockAsync(new(Guid.NewGuid(), partition, "legacy", "private block", 1, 100, true,
            MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow));
        await _store.WriteEdgeAsync(new(Guid.NewGuid(), partition, secret.Id, entity.Id, "private edge", entity.Id,
            MemoryTrustTier.AgentInference, 1, DateTimeOffset.UtcNow.AddMinutes(-1), null, true, DateTimeOffset.UtcNow));
        await _store.WriteProcedureAsync(new(Guid.NewGuid(), partition, secret.Id, "private", "private procedure", null,
            1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, DateTimeOffset.UtcNow.AddMinutes(-1), null, DateTimeOffset.UtcNow));
        await _store.WriteEmbeddingAsync(new(Guid.NewGuid(), partition, secret.Id, MemoryLayer.Episodic, [1, 0], "test", DateTimeOffset.UtcNow));

        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Export, "export", partition), default);
        Assert.True(response.Succeeded, response.Error);
        var export = JsonSerializer.Deserialize<MemoryExport>(response.Payload.Span, JsonOptions)!;
        Assert.Equal(allowed.Id, Assert.Single(export.Episodes).Id);
        Assert.Equal(entity.Id, Assert.Single(export.Entities).Id);
        var claim = Assert.Single(export.Claims);
        Assert.Equal(allowedClaim.Id, claim.Id);
        Assert.Equal(MemorySensitivity.Personal, claim.Sensitivity);
        Assert.Empty(export.Edges);
        Assert.Empty(export.Procedures);
        Assert.Empty(export.Blocks);
        Assert.Empty(export.Embeddings!);
    }

    [Theory]
    [InlineData("write-claim")]
    [InlineData("write-edge")]
    [InlineData("write-procedure")]
    [InlineData("write-embedding")]
    public async Task WritesCannotReferenceForeignEvidence(string operation)
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(EmployeePartition() with { TenantId = Guid.NewGuid().ToString("D") }, "foreign memory");
        var entity = await EntityAsync(partition);
        var now = DateTimeOffset.UtcNow;
        object record = operation switch
        {
            "write-claim" => Claim(source, entity) with { Partition = partition },
            "write-edge" => new MemoryEdge(Guid.NewGuid(), partition, source.Id, entity.Id, "memory", entity.Id,
                MemoryTrustTier.AgentInference, 1, now, null, true, now),
            "write-procedure" => new ProceduralMemory(Guid.NewGuid(), partition, source.Id, "memory", "memory", null, 1,
                MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, now, null, now),
            _ => new MemoryEmbedding(Guid.NewGuid(), partition, source.Id, MemoryLayer.Episodic, [1, 0], "test", now)
        };
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, operation, record), default);
        Assert.False(response.Succeeded);
        var export = await _store.ExportAsync(partition);
        Assert.Empty(export.Claims);
        Assert.Empty(export.Edges);
        Assert.Empty(export.Procedures);
        Assert.Empty(export.Embeddings!);
    }

    [Fact]
    public async Task SourceLessEntityAndBlockWritesRequireTrustedReview()
    {
        var partition = EmployeePartition();
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "learned:topic", "memory", [], "memory:key", false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Public };
        var block = new MemoryBlock(Guid.NewGuid(), partition, "memory", "memory content", 1, 100, true,
            MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Public };
        Assert.False((await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "upsert-entity", entity), default)).Succeeded);
        Assert.False((await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-block", block), default)).Succeeded);
        var export = await _store.ExportAsync(partition);
        Assert.Empty(export.Entities);
        Assert.Empty(export.Blocks);
        Assert.Empty(JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(partition)).Payload.Span, JsonOptions)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferReadValidatesSourcesBeforeReturningDebrief(bool restricted)
    {
        var target = EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), _employee.ToString("D"), "csweet");
        var sourceNamespace = restricted ? target : EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), Guid.NewGuid().ToString("D"), "csweet");
        var source = await AppendAsync(sourceNamespace.Partition, "memory transfer evidence",
            restricted ? MemorySensitivity.Restricted : MemorySensitivity.Internal);
        var package = new KnowledgeTransferPackage(Guid.NewGuid(), _organization.ToString("D"), sourceNamespace.AudienceId,
            _employee.ToString("D"), [sourceNamespace], target, "private debrief",
            [new(source.Id, sourceNamespace.Partition, MemoryLayer.Episodic, MemoryClaimKind.Observation,
                source.Content, MemorySensitivity.Public, MemoryTrustTier.Authoritative, [source.Id], $"memory:{source.Id:N}")],
            MemorySensitivity.Public, KnowledgeTransferStatus.Approved, DateTimeOffset.UtcNow, _employee.ToString("D"));
        await _store.WriteKnowledgeTransferAsync(package);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "get-knowledge-transfer",
            new { packageId = package.Id }), default);
        Assert.DoesNotContain("private debrief", response.Payload.ToStringUtf8());
        if (restricted) Assert.Equal("null", response.Payload.ToStringUtf8());
        else Assert.False(response.Succeeded);
    }

    private async Task<MemoryEntity> EntityAsync(MemoryPartition partition, MemorySensitivity sensitivity = MemorySensitivity.Internal)
    {
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "learned:topic", $"memory {Guid.NewGuid():N}", [],
            Guid.NewGuid().ToString("D"), false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = sensitivity };
        await _store.UpsertEntityAsync(entity);
        return entity;
    }

    [Fact]
    public async Task ClaimWritesRetainSourceSensitivityAndRejectForeignSupersession()
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "memory evidence", MemorySensitivity.Personal);
        var entity = await EntityAsync(partition);
        var claim = Claim(source, entity);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-claim", claim), default);
        Assert.True(response.Succeeded, response.Error);
        var writtenId = JsonSerializer.Deserialize<MemoryWriteResult>(response.Payload.Span, JsonOptions)!.Id;
        Assert.Equal(MemorySensitivity.Personal, (await _store.GetClaimAsync(writtenId))!.Sensitivity);

        var foreign = claim with { Id = Guid.NewGuid(), Partition = partition with { TenantId = Guid.NewGuid().ToString("D") } };
        await _store.WriteClaimAsync(foreign);
        var denied = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-claim",
            claim with { Id = Guid.NewGuid(), SupersedesClaimId = foreign.Id }), default);
        Assert.False(denied.Succeeded);
        denied = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Manage, "supersede-claim",
            new { claimId = writtenId, supersededByClaimId = foreign.Id, validTo = DateTimeOffset.UtcNow }), default);
        Assert.False(denied.Succeeded);
        Assert.Null((await _store.GetClaimAsync(writtenId))!.ValidTo);
    }

    private static MemoryClaim Claim(MemoryEpisode source, MemoryEntity entity) => new(Guid.NewGuid(), source.Partition,
        source.Id, entity.Id, "memory", null, "derived memory", MemoryTrustTier.AgentInference,
        MemoryConfirmationState.NotRequired, MemorySensitivity.Public, 1, 1, DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow);

    private MemoryPartition EmployeePartition(string app = "csweet") =>
        EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), _employee.ToString("D"), app).Partition;

    private PlatformMemoryCapabilityHandler Handler() => new(_store,
        NullLogger<PlatformMemoryCapabilityHandler>.Instance, new AgentMemoryIdentityResolver(_db), new QueryContractEvidence());

    private AgentSession Session() => new("session", "agent", _installation.ToString("D"),
        _organization.ToString("D"), "runtime", "tick", new AuthorizedAgentGrant(new HashSet<string>(),
            new HashSet<string>(), new HashSet<string> { CSweetMemoryCapabilities.Query, CSweetMemoryCapabilities.Write,
                CSweetMemoryCapabilities.Manage, CSweetMemoryCapabilities.Export }, 1));

    private Task<CapabilityResult> SearchAsync(MemoryPartition partition) => Handler().HandleAsync(Session(),
        Request(CSweetMemoryCapabilities.Query, "search", new MemorySearchRequest(partition, MemoryScope.Agent, "memory")), default);

    private static RequestCapability Request<T>(string capability, string operation, T payload) => new()
    {
        RequestId = "test", Capability = capability,
        Payload = JsonPayload.From(new CSweetMemoryCommand(operation, JsonSerializer.SerializeToElement(payload, JsonOptions)), JsonOptions)
    };

    private async Task<MemoryEpisode> AppendAsync(MemoryPartition partition, string content,
        MemorySensitivity sensitivity = MemorySensitivity.Internal)
    {
        var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Agent, content, "text/plain",
            new MemorySource("user", "source"), "checksum", DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow, Guid.NewGuid().ToString(), Sensitivity: sensitivity);
        await _store.AppendEpisodeAsync(episode);
        return episode;
    }
}
