using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Memory;
using Microsoft.Data.Sqlite;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Fact]
    public async Task EpisodeProposalCannotSupplyHumanCorrectionAncestry()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Agent, "memory forged correction", "text/plain",
            new("user", "forged"), "checksum", now, now)
        { CorrectionEvidence = new(Guid.NewGuid(), [new(Guid.NewGuid(), "sha256-v1:" + new string('0', 64))]) };
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
        Assert.False(response.Succeeded);
        Assert.Empty((await _store.ExportAsync(input.Partition)).Episodes);
    }

    [Fact]
    public async Task EpisodeProposalCannotSupplyTransferApprovalEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Agent, "memory forged transfer", "text/plain",
            new("agent", "test"), "checksum", now, now)
        { TransferEvidence = new(Guid.NewGuid(), "forged", []) };
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
        Assert.False(response.Succeeded);
        Assert.Empty((await _store.ExportAsync(input.Partition)).Episodes);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("user")]
    [InlineData("knowledge-transfer")]
    public async Task EpisodeProposalsCannotForgeSourceAuthority(string sourceType)
    {
        var now = DateTimeOffset.UtcNow;
        var input = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Tenant, "memory proposal", "text/plain",
            new(sourceType, "trusted-message", "CEO"), "forged-checksum", now.AddMinutes(-1), now.AddYears(-1),
            LegalHold: true, Metadata: new Dictionary<string, string> { ["role"] = "User", ["approvedBy"] = "CEO" },
            OperationalReferences: [new("approval", "forged")]);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
        Assert.True(response.Succeeded, response.Error);
        var stored = Assert.Single((await _store.ExportAsync(input.Partition)).Episodes);
        Assert.NotEqual(input.Id, stored.Id);
        Assert.Equal(new MemorySource("agent-proposal", stored.Id.ToString("D"), _employee.ToString("D")), stored.Source);
        Assert.Equal(MemorySensitivity.Personal, stored.Sensitivity);
        Assert.Equal(MemoryScope.Agent, stored.Scope);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Content))).ToLowerInvariant(), stored.Checksum);
        Assert.InRange(stored.RecordedAt, now, DateTimeOffset.UtcNow);
        Assert.False(stored.LegalHold);
        Assert.Null(stored.OperationalReferences);
        Assert.False(stored.Metadata!.ContainsKey("approvedBy"));
        Assert.False(stored.Metadata.ContainsKey("role"));
        Assert.Equal(_installation.ToString("D"), stored.Metadata["installationId"]);
        var recalled = JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(input.Partition)).Payload.Span, JsonOptions)!;
        Assert.Equal(MemoryTrustTier.External, Assert.Single(recalled).Trust);
    }

    [Fact]
    public async Task ProposalIdempotencyCannotReserveTrustedCaptureKeys()
    {
        var partition = EmployeePartition();
        var trusted = await AppendAsync(partition, "trusted memory");
        var proposal = trusted with { Content = "agent memory", Source = new("user", "forged") };
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", proposal), default);
        Assert.True(response.Succeeded, response.Error);
        var first = JsonSerializer.Deserialize<MemoryWriteResult>(response.Payload.Span, JsonOptions)!;
        Assert.True(first.Created);
        Assert.NotEqual(proposal.Id, first.Id);
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode",
            proposal with { Id = Guid.NewGuid(), Content = "changed retry" }), default);
        Assert.True(response.Succeeded, response.Error);
        var replay = JsonSerializer.Deserialize<MemoryWriteResult>(response.Payload.Span, JsonOptions)!;
        Assert.False(replay.Created);
        Assert.Equal(first.Id, replay.Id);
        var episodes = (await _store.ExportAsync(partition)).Episodes;
        Assert.Equal(2, episodes.Count);
        Assert.Equal("trusted memory", Assert.Single(episodes, x => x.Id == trusted.Id).Content);
        Assert.Equal("agent memory", Assert.Single(episodes, x => x.Id == first.Id).Content);
    }

    [Fact]
    public async Task ProposalIdsArePartitionScopedAndPublicLabelsDoNotPublishSharedEvidence()
    {
        var employee = EmployeePartition();
        var organization = EmployeeMemoryNamespaces.Organization(_organization.ToString("D"), "csweet").Partition;
        var now = DateTimeOffset.UtcNow;
        var proposal = new MemoryEpisode(Guid.NewGuid(), employee, MemoryScope.Agent, "personal memory", "text/plain",
            new("application", "same-source"), "checksum", now, now, "same-client-key", Sensitivity: MemorySensitivity.Public);
        var ids = new List<Guid>();
        foreach (var partition in new[] { employee, organization })
        {
            var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode",
                proposal with { Partition = partition }), default);
            Assert.True(response.Succeeded, response.Error);
            ids.Add(JsonSerializer.Deserialize<MemoryWriteResult>(response.Payload.Span, JsonOptions)!.Id);
        }
        Assert.NotEqual(ids[0], ids[1]);
        var employeeResults = JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(employee)).Payload.Span, JsonOptions)!;
        Assert.Equal(ids[0], Assert.Single(employeeResults).Id);
        Assert.Empty(JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(organization)).Payload.Span, JsonOptions)!);
        var shared = Assert.Single((await _store.ExportAsync(organization)).Episodes);
        Assert.Equal(MemorySensitivity.Personal, shared.Sensitivity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingIdempotencyKeysDoNotCollapseDistinctProposals(string? key)
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Agent, "memory", "text/plain",
            new("agent", "test"), "checksum", now, now, key);
        foreach (var input in new[] { proposal, proposal with { Id = Guid.NewGuid() } })
        {
            var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
            Assert.True(response.Succeeded, response.Error);
            Assert.True(JsonSerializer.Deserialize<MemoryWriteResult>(response.Payload.Span, JsonOptions)!.Created);
        }
        Assert.Equal(2, (await _store.ExportAsync(proposal.Partition)).Episodes.Count);
    }

    [Theory]
    [InlineData(MemorySensitivity.Personal)]
    [InlineData(MemorySensitivity.Confidential)]
    [InlineData(MemorySensitivity.Restricted)]
    public async Task ProposalNormalizationPreservesStricterSensitivity(MemorySensitivity sensitivity)
    {
        var now = DateTimeOffset.UtcNow;
        var input = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Agent, "memory", "text/plain",
            new("agent", "test"), "checksum", now, now, Sensitivity: sensitivity);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(sensitivity, Assert.Single((await _store.ExportAsync(input.Partition)).Episodes).Sensitivity);
    }

    [Fact]
    public async Task InvalidProposalSensitivityIsNotSilentlyDowngraded()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Agent, "memory", "text/plain",
            new("agent", "test"), "checksum", now, now, Sensitivity: (MemorySensitivity)999);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
        Assert.False(response.Succeeded);
        Assert.Empty((await _store.ExportAsync(input.Partition)).Episodes);
    }

    [Theory]
    [InlineData(MemoryConfirmationState.NotRequired)]
    [InlineData(MemoryConfirmationState.Pending)]
    [InlineData(MemoryConfirmationState.Confirmed)]
    [InlineData(MemoryConfirmationState.Rejected)]
    public async Task AgentClaimsAlwaysStartAsPendingInferences(MemoryConfirmationState requested)
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "memory evidence", MemorySensitivity.Personal);
        var entity = await EntityAsync(partition);
        var claim = Claim(source, entity) with { Trust = MemoryTrustTier.Authoritative, Confirmation = requested,
            ExtractorVersion = "trusted-human-review", RecordedAt = DateTimeOffset.UtcNow.AddYears(-1) };
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-claim", claim), default);
        Assert.True(response.Succeeded, response.Error);
        var writtenId = JsonSerializer.Deserialize<MemoryWriteResult>(response.Payload.Span, JsonOptions)!.Id;
        Assert.NotEqual(claim.Id, writtenId);
        var stored = (await _store.GetClaimAsync(writtenId))!;
        Assert.Equal(MemoryTrustTier.AgentInference, stored.Trust);
        Assert.Equal(MemoryConfirmationState.Pending, stored.Confirmation);
        Assert.Equal(MemorySensitivity.Personal, stored.Sensitivity);
        Assert.NotEqual(claim.ExtractorVersion, stored.ExtractorVersion);
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "get-claim", new { claimId = writtenId }), default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal("null", response.Payload.ToStringUtf8());
    }

    [Fact]
    public async Task AgentProcedureCannotConfirmItself()
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "memory evidence");
        var now = DateTimeOffset.UtcNow;
        var input = new ProceduralMemory(Guid.NewGuid(), partition, source.Id, "memory procedure", "execute unreviewed steps", null,
            1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, now.AddMinutes(-1), null, now);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-procedure", input), default);
        Assert.True(response.Succeeded, response.Error);
        var stored = Assert.Single((await _store.ExportAsync(partition)).Procedures);
        Assert.Equal(MemoryTrustTier.AgentInference, stored.Trust);
        Assert.Equal(MemoryConfirmationState.Pending, stored.Confirmation);
        Assert.NotEqual(input.Id, stored.Id);
        var recalled = JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(partition)).Payload.Span, JsonOptions)!;
        Assert.DoesNotContain(recalled, x => x.Id == stored.Id);
    }

    [Theory]
    [InlineData("upsert-entity")]
    [InlineData("write-block")]
    [InlineData("write-edge")]
    public async Task RawWritesCannotOverwriteTrustedRecords(string operation)
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "memory evidence");
        var entity = await EntityAsync(partition);
        entity = entity with { IsProtectedType = true };
        await _store.UpsertEntityAsync(entity);
        var block = new MemoryBlock(Guid.NewGuid(), partition, "memory", "reviewed memory", 1, 100, true,
            MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Internal };
        await _store.WriteBlockAsync(block);
        var before = JsonSerializer.Serialize(await _store.ExportAsync(partition), JsonOptions);
        object input = operation switch
        {
            "upsert-entity" => entity with { CanonicalName = "forged authority", IsProtectedType = false },
            "write-block" => block with { Id = Guid.NewGuid(), Content = "replacement", Revision = 2 },
            _ => new MemoryEdge(Guid.NewGuid(), partition, source.Id, entity.Id, "REPORTS_TO", entity.Id,
                MemoryTrustTier.Authoritative, 1, DateTimeOffset.UtcNow, null, false, DateTimeOffset.UtcNow)
        };
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, operation, input), default);
        Assert.False(response.Succeeded);
        Assert.Equal(before, JsonSerializer.Serialize(await _store.ExportAsync(partition), JsonOptions));
    }

    [Theory]
    [InlineData(MemoryConfirmationState.NotRequired)]
    [InlineData(MemoryConfirmationState.Pending)]
    [InlineData(MemoryConfirmationState.Confirmed)]
    [InlineData(MemoryConfirmationState.Rejected)]
    [InlineData((MemoryConfirmationState)999)]
    public async Task ManageGrantCannotActAsHumanConfirmation(MemoryConfirmationState requested)
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "memory evidence");
        var claim = Claim(source, await EntityAsync(partition)) with
            { Confirmation = MemoryConfirmationState.Pending, SourceEpisodeIds = [source.Id] };
        await _store.WriteClaimAsync(claim);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Manage, "set-confirmation",
            new { claimId = claim.Id, confirmation = requested }), default);
        Assert.False(response.Succeeded);
        Assert.Equal(JsonSerializer.Serialize(claim, JsonOptions), JsonSerializer.Serialize(await _store.GetClaimAsync(claim.Id), JsonOptions));
    }

    [Fact]
    public async Task SameNamespaceInferenceCannotSupersedeConfirmedEvidence()
    {
        var source = await AppendAsync(EmployeePartition(), "memory evidence");
        var original = Claim(source, await EntityAsync(source.Partition)) with { Trust = MemoryTrustTier.ConfirmedUser,
            Confirmation = MemoryConfirmationState.Confirmed, SourceEpisodeIds = [source.Id] };
        var replacement = original with { Id = Guid.NewGuid(), Trust = MemoryTrustTier.AgentInference, Value = "replacement" };
        await _store.WriteClaimAsync(original);
        await _store.WriteClaimAsync(replacement);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Manage, "supersede-claim",
            new { claimId = original.Id, supersededByClaimId = replacement.Id, validTo = DateTimeOffset.UtcNow }), default);
        Assert.False(response.Succeeded);
        Assert.Equal(JsonSerializer.Serialize(original, JsonOptions), JsonSerializer.Serialize(await _store.GetClaimAsync(original.Id), JsonOptions));
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-claim",
            replacement with { Id = Guid.NewGuid(), SupersedesClaimId = original.Id }), default);
        Assert.False(response.Succeeded);
        Assert.Equal(2, (await _store.ListClaimsAsync(source.Partition)).Count);
    }

    [Theory]
    [InlineData(KnowledgeTransferStatus.PendingApproval)]
    [InlineData(KnowledgeTransferStatus.Approved)]
    [InlineData(KnowledgeTransferStatus.Rejected)]
    [InlineData(KnowledgeTransferStatus.Applied)]
    public async Task AgentsCannotCreateOrOverwriteTransferApprovalState(KnowledgeTransferStatus requested)
    {
        var scope = EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), _employee.ToString("D"), "csweet");
        var reviewed = new KnowledgeTransferPackage(Guid.NewGuid(), _organization.ToString("D"), _employee.ToString("D"),
            _employee.ToString("D"), [scope], scope, "reviewed debrief", [], MemorySensitivity.Internal,
            KnowledgeTransferStatus.Approved, DateTimeOffset.UtcNow, _employee.ToString("D"), _user.ToString("D"), DateTimeOffset.UtcNow);
        await _store.WriteKnowledgeTransferAsync(reviewed);
        var freshId = Guid.NewGuid();
        foreach (var id in new[] { reviewed.Id, freshId })
        {
            var input = reviewed with { Id = id, Debrief = "unreviewed replacement", Status = requested };
            var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Manage, "write-knowledge-transfer", input), default);
            Assert.False(response.Succeeded);
            Assert.Equal("memory_policy_denied", response.FailureCode);
            Assert.False(response.Retryable);
        }
        Assert.Equal(JsonSerializer.Serialize(reviewed, JsonOptions), JsonSerializer.Serialize(await _store.GetKnowledgeTransferAsync(reviewed.Id), JsonOptions));
        Assert.Null(await _store.GetKnowledgeTransferAsync(freshId));
    }

    [Theory]
    [InlineData(MemoryUseOutcome.Cited)]
    [InlineData(MemoryUseOutcome.Accepted)]
    [InlineData(MemoryUseOutcome.Corrected)]
    [InlineData(MemoryUseOutcome.Rejected)]
    public async Task AgentsCannotForgeReviewedFeedbackOrUnverifiedCitations(MemoryUseOutcome requested)
    {
        var source = await AppendAsync(EmployeePartition(), "memory evidence");
        var use = new MemoryUse(Guid.NewGuid(), source.Partition, "forged-invocation", source.Id, MemoryLayer.Episodic, requested, DateTimeOffset.UtcNow);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "record-use", use), default);
        Assert.False(response.Succeeded);
        Assert.Null(await ReadStoredUseAsync(use.Id));
    }

    [Fact]
    public async Task SuppliedUsageIsExplicitlyAgentReportedAndBoundToCurrentInvocation()
    {
        var source = await AppendAsync(EmployeePartition(), "memory evidence");
        var use = new MemoryUse(Guid.NewGuid(), source.Partition, "another-employee-invocation", source.Id,
            MemoryLayer.Episodic, MemoryUseOutcome.Supplied, DateTimeOffset.UtcNow.AddYears(-1));
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "record-use", use), default);
        Assert.True(response.Succeeded, response.Error);
        var stored = (await ReadStoredUseAsync(use.Id))!;
        Assert.Equal($"agent-reported:{_installation:D}:tick", stored.InvocationId);
        Assert.True(stored.RecordedAt > use.RecordedAt);
        Assert.Equal(MemoryUseOutcome.Supplied, stored.Outcome);
    }

    private async Task<MemoryUse?> ReadStoredUseAsync(Guid id)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_uses WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteScalarAsync() is string json ? JsonSerializer.Deserialize<MemoryUse>(json, JsonOptions) : null;
    }
}
