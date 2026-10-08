using System.Text.Json;
using CSweet.Memory;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Fact]
    public async Task BrokerRecallAndExportWithholdTransferAfterOriginalSourceDeletion()
    {
        var sourceId = Guid.NewGuid().ToString("D");
        var source = EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), sourceId, "csweet");
        var target = EmployeeMemoryNamespaces.Employee(_organization.ToString("D"), _employee.ToString("D"), "csweet");
        var access = new MemoryAccessContext(new(_organization.ToString("D"), _user.ToString("D")), "test", "transfer");
        var engine = new MemoryEngine(_store, Microsoft.Extensions.Options.Options.Create(new AgentMemoryOptions()),
            authorizer: new AllowAllMemoryScopeAuthorizer());
        var original = await engine.IngestAsync(new(source.Partition, source.Scope, "memory handoff checklist",
            new("user", "source"), Sensitivity: MemorySensitivity.Personal, Access: access));
        var draft = await engine.PrepareKnowledgeTransferAsync(new(sourceId, _employee.ToString("D"), [source], target, access,
            "Approved memory", Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
        await engine.ApproveKnowledgeTransferAsync(new(draft.Id, access, true));
        await engine.ApplyKnowledgeTransferAsync(new(draft.Id, access));
        Assert.Single(JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(target.Partition)).Payload.Span, JsonOptions)!);
        // The receiving agent has only its own namespace. Trusted transfer provenance resolves the original internally.
        Assert.False((await SearchAsync(source.Partition)).Succeeded);
        var before = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Export, "export", target.Partition), default);
        Assert.True(before.Succeeded, before.Error);
        Assert.Single(JsonSerializer.Deserialize<MemoryExport>(before.Payload.Span, JsonOptions)!.Episodes);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_episodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", original.Id.ToString("D"));
        await command.ExecuteNonQueryAsync();
        Assert.Empty(JsonSerializer.Deserialize<MemoryCandidate[]>((await SearchAsync(target.Partition)).Payload.Span, JsonOptions)!);
        var after = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Export, "export", target.Partition), default);
        Assert.True(after.Succeeded, after.Error);
        Assert.Empty(JsonSerializer.Deserialize<MemoryExport>(after.Payload.Span, JsonOptions)!.Episodes);
    }

    [Theory]
    [InlineData("[2]", true)]
    [InlineData("[]", true)]
    [InlineData("[999]", false)]
    [InlineData("[2,2,2,2,2]", false)]
    [InlineData("[\"Semantic\"]", false)]
    [InlineData("{}", false)]
    public async Task BrokerLayerFiltersAreDeserializedAndValidated(string layers, bool valid)
    {
        var payload = JsonSerializer.SerializeToNode(new { partition = EmployeePartition(), scope = MemoryScope.Agent, query = "memory" }, JsonOptions)!;
        payload["layers"] = System.Text.Json.Nodes.JsonNode.Parse(layers);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "search", payload), default);
        Assert.Equal(valid, response.Succeeded);
        if (!valid) Assert.DoesNotContain("temporarily unavailable", response.Error ?? "");
    }

    [Theory]
    [InlineData("confidential")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("changed")]
    public async Task AdditionalDerivativeEvidenceIsCheckedByBrokerReadsAndProposals(string scenario)
    {
        var partition = EmployeePartition();
        var primary = await AppendAsync(partition, "visible memory evidence");
        var entity = await EntityAsync(partition);
        var contributorId = scenario == "missing" ? Guid.NewGuid() : (await AppendAsync(
            scenario == "foreign" ? partition with { AgentId = Guid.NewGuid().ToString("D") } : partition,
            "hidden evidence", scenario == "changed" ? MemorySensitivity.Internal : MemorySensitivity.Confidential)).Id;
        if (scenario == "changed")
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE memory_episodes SET payload=json_set(payload,'$.content','altered evidence') WHERE id=$id";
            command.Parameters.AddWithValue("$id", contributorId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var claim = Claim(primary, entity) with { SourceEpisodeIds = [contributorId] };
        var procedure = new ProceduralMemory(Guid.NewGuid(), partition, primary.Id, "memory", "private memory procedure",
            null, 1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, DateTimeOffset.UtcNow.AddMinutes(-1), null,
            DateTimeOffset.UtcNow) { SourceEpisodeIds = [contributorId] };
        await _store.WriteClaimAsync(claim);
        await _store.WriteProcedureAsync(procedure);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "get-claim", new { claimId = claim.Id }), default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal("null", response.Payload.ToStringUtf8());
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "list-claims", partition), default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Empty(JsonSerializer.Deserialize<MemoryClaim[]>(response.Payload.Span, JsonOptions)!);
        response = await SearchAsync(partition);
        Assert.True(response.Succeeded, response.Error);
        Assert.DoesNotContain(JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!, x => x.Id == claim.Id || x.Id == procedure.Id);
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Export, "export", partition), default);
        Assert.True(response.Succeeded, response.Error);
        var export = JsonSerializer.Deserialize<MemoryExport>(response.Payload.Span, JsonOptions)!;
        Assert.Empty(export.Claims); Assert.Empty(export.Procedures);
        if (scenario == "confidential") return; // A high-sensitivity pending proposal is permitted, but never recalled.
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-claim", claim), default);
        Assert.False(response.Succeeded);
        response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-procedure", procedure), default);
        Assert.False(response.Succeeded);
    }

    [Fact]
    public async Task GraphRecallCannotCrossAConfidentialBridgeToExposeAnInternalEdge()
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "ordinary evidence");
        var secret = await AppendAsync(partition, "hidden evidence", MemorySensitivity.Confidential);
        var root = (await EntityAsync(partition)) with { CanonicalName = "Alpha" };
        await _store.UpsertEntityAsync(root);
        var middle = root with { Id = Guid.NewGuid(), CanonicalName = "Beta", ApplicationKey = null };
        var end = middle with { Id = Guid.NewGuid(), CanonicalName = "Gamma" };
        await _store.UpsertEntityAsync(middle); await _store.UpsertEntityAsync(end);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var bridge = new MemoryEdge(Guid.NewGuid(), partition, secret.Id, root.Id, "links", middle.Id,
            MemoryTrustTier.UnconfirmedUser, 1, now, null, false, now);
        var onward = bridge with { Id = Guid.NewGuid(), EpisodeId = source.Id, FromEntityId = middle.Id, ToEntityId = end.Id };
        await _store.WriteEdgeAsync(bridge); await _store.WriteEdgeAsync(onward);
        var request = Request(CSweetMemoryCapabilities.Query, "search", new MemorySearchRequest(partition,
            MemoryScope.Agent, "Alpha", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic }));
        var response = await Handler().HandleAsync(Session(), request, default);
        Assert.True(response.Succeeded, response.Error);
        Assert.Empty(JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!);
        await _store.WriteEdgeAsync(bridge with { Id = Guid.NewGuid(), EpisodeId = source.Id });
        response = await Handler().HandleAsync(Session(), request, default);
        Assert.True(response.Succeeded, response.Error);
        var result = Assert.Single(JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!, x => x.Id == onward.Id);
        Assert.DoesNotContain(secret.Id, result.EpisodeIds);
    }

    [Fact]
    public async Task EntityAndBlockLineageIsEnforcedAcrossBrokerReads()
    {
        var partition = EmployeePartition();
        var hiddenSource = await AppendAsync(partition, "confidential memory evidence", MemorySensitivity.Confidential);
        var visibleSource = await AppendAsync(partition, "visible memory evidence");
        var entity = await EntityAsync(partition);
        await _store.UpsertEntityAsync(entity with { SourceEpisodeIds = [hiddenSource.Id], ApplicationKey = "known" });
        var claim = Claim(visibleSource, entity);
        await _store.WriteClaimAsync(claim);
        await _store.WriteBlockAsync(new(Guid.NewGuid(), partition, "memory", "private summary", 1, 100, true,
            MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow)
            { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [hiddenSource.Id] });

        var search = await SearchAsync(partition);
        Assert.True(search.Succeeded, search.Error);
        Assert.Equal(visibleSource.Id, Assert.Single(JsonSerializer.Deserialize<MemoryCandidate[]>(search.Payload.Span, JsonOptions)!).Id);
        foreach (var (operation, payload) in new (string, object)[] {
            ("find-entity", new { partition, canonicalName = entity.CanonicalName }),
            ("find-entity-by-application-key", new { partition, applicationKey = "known" }),
            ("get-claim", new { claimId = claim.Id }) })
        {
            var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, operation, payload), default);
            Assert.True(response.Succeeded, response.Error);
            Assert.Equal("null", response.Payload.ToStringUtf8());
        }
        var export = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Export, "export", partition), default);
        Assert.True(export.Succeeded, export.Error);
        var projected = JsonSerializer.Deserialize<MemoryExport>(export.Payload.Span, JsonOptions)!;
        Assert.Empty(projected.Entities);
        Assert.Empty(projected.Claims);
        Assert.Empty(projected.Blocks);
    }
}
