using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.AgentHost.Broker;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Fact]
    public Task SqliteBrokerReplayAndRecallContract() => BrokerDatabaseContract(_store);

    [MemoryPostgresFact]
    public async Task PostgreSqlBrokerReplayAndRecallContract()
    {
        await using var store = new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!);
        try { await BrokerDatabaseContract(store); }
        finally { await store.DeleteScopeAsync(EmployeePartition()); }
    }

    private async Task BrokerDatabaseContract(IMemoryStore store)
    {
        var handler = new PlatformMemoryCapabilityHandler(store, NullLogger<PlatformMemoryCapabilityHandler>.Instance,
            new AgentMemoryIdentityResolver(_db), new QueryContractEvidence());
        var partition = EmployeePartition();
        var now = DateTimeOffset.UtcNow;
        var source = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Agent, "Alice selected PostgreSQL",
            "text/plain", new("user", "fixture"), "checksum", now.AddDays(-1), now, Sensitivity: MemorySensitivity.Personal);
        await store.AppendEpisodeAsync(source);
        await store.AppendEpisodeAsync(source with { Id = Guid.NewGuid(), Content = "Alice restricted secret", Sensitivity = MemorySensitivity.Restricted });
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now)
            { Sensitivity = MemorySensitivity.Personal };
        await store.UpsertEntityAsync(entity);
        var proposal = Claim(source, entity) with { Predicate = "selected", Value = "PostgreSQL" };
        var request = Request(CSweetMemoryCapabilities.Write, "write-claim", proposal);
        var first = await handler.HandleAsync(Session(), request, default);
        Assert.True(first.Succeeded, first.Error);
        var written = JsonSerializer.Deserialize<MemoryWriteResult>(first.Payload.Span, JsonOptions)!;
        Assert.True(written.Created);
        var replay = await handler.HandleAsync(Session(), request, default);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.False(JsonSerializer.Deserialize<MemoryWriteResult>(replay.Payload.Span, JsonOptions)!.Created);
        var conflict = await handler.HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "write-claim",
            proposal with { Value = "Overwrite attempt" }), default);
        Assert.False(conflict.Succeeded);
        Assert.Equal("memory_write_conflict", conflict.FailureCode);
        Assert.False(conflict.Retryable);
        // Previous clients send the six identity fields and legacy Key, without StorageKey.
        // The current broker must derive the new storage identity, not require a client upgrade.
        var legacyWire = JsonNode.Parse(JsonSerializer.Serialize(source with
            { Id = Guid.NewGuid(), Content = "Legacy wire compatibility sample" }, JsonOptions))!;
        legacyWire["partition"]!.AsObject().Remove("storageKey");
        var adapted = await handler.HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", legacyWire), default);
        Assert.True(adapted.Succeeded, adapted.Error);
        var adaptedId = JsonSerializer.Deserialize<MemoryWriteResult>(adapted.Payload.Span, JsonOptions)!.Id;
        Assert.Equal(partition, (await ((IMemorySourceReader)store).GetEpisodeAsync(partition, adaptedId))!.Partition);
        var search = Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(partition, MemoryScope.Agent, "What did Alice select?"));
        var response = await handler.HandleAsync(Session(), search, default);
        Assert.True(response.Succeeded, response.Error);
        var found = JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!;
        Assert.Equal(source.Id, Assert.Single(found).Id);
        // Current trusted review state is visible, and an older proposal cannot undo it.
        await store.SetClaimConfirmationAsync(written.Id, MemoryConfirmationState.Confirmed);
        var reviewed = await handler.HandleAsync(Session(), search, default);
        Assert.Contains(JsonSerializer.Deserialize<MemoryCandidate[]>(reviewed.Payload.Span, JsonOptions)!, x => x.Id == written.Id);
        Assert.False((await handler.HandleAsync(Session(), request, default)).Succeeded);
        (await _db.AgentInstallations.SingleAsync()).IsEnabled = false;
        await _db.SaveChangesAsync();
        Assert.False((await handler.HandleAsync(Session(), search, default)).Succeeded);
    }
}
