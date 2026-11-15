using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentOperatingStateCapabilityTests
{
    [Fact]
    public async Task MaximumLengthStateIdentityFitsItsReceiptWithoutShrinkingThePublicContract()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
        var input = new AgentOperatingStateWriteRequest(new string('k', AgentOperatingStateKey.MaximumLength), "test.v1", 1, "Active",
            new Dictionary<string,string>(), [], "basis", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new { value = "retained" }), null, "write");
        Assert.True((await InvokeAsync(handler, Session(Guid.NewGuid(), Guid.NewGuid()), PlatformCapabilities.AgentOperatingStateWrite, input)).Succeeded);
        Assert.True((await db.DurableMutationReceipts.SingleAsync()).ResourceKey.Length <= 160);
        Assert.Equal(input.StateKey, (await db.PluginOperationalStates.SingleAsync()).ExternalKey);
    }

    [Fact]
    public async Task ReceiptRejectsChangedContentAndReplaysAcrossInterveningWritesWithoutRollingBackState()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
        var session = Session(Guid.NewGuid(), Guid.NewGuid());
        var first = new AgentOperatingStateWriteRequest("planning", "test.v1", 1, "Active", new Dictionary<string,string>(), [], "basis", [], Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { references = new[] { "original" } }), null, "same-clock:first");
        Assert.True((await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, first)).Succeeded);
        var changedKeyReuse = await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, first with {
            Payload = JsonSerializer.SerializeToElement(new { references = new[] { "original", "commission" } }) });
        Assert.False(changedKeyReuse.Succeeded); Assert.Contains("idempotency.content_mismatch", changedKeyReuse.Error);
        Assert.True((await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, first with {
            ExpectedRevision = 1, IdempotencyKey = "same-clock:second", Payload = JsonSerializer.SerializeToElement(new { references = new[] { "original", "commission" } }) })).Succeeded);
        db.ChangeTracker.Clear();
        var replay = await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, first);
        Assert.True(replay.Succeeded); Assert.Equal(1, JsonSerializer.Deserialize<AgentOperatingStateResponse>(replay.Payload.ToByteArray(), JsonOptions)!.Revision);
        var current = await db.PluginOperationalStates.SingleAsync(); Assert.Equal(2, current.Revision); Assert.Contains("commission", current.PayloadJson);
        Assert.Equal(2, await db.DurableMutationReceipts.CountAsync());
    }
}
