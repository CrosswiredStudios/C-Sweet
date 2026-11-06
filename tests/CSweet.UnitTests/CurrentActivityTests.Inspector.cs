using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class CurrentActivityTests
{
    [Fact]
    public async Task InspectorUsesExactAuthorizedAttemptAndRevocationStopsReads()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var ours = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        await f.RunAsync(f.Attempt.Id, Guid.NewGuid());
        await f.ChunkAsync(ours, 0, "First "); await f.ChunkAsync(ours, 1, "second");
        var log = await f.Db.AgentRunLogs.SingleAsync(x => x.Id == ours); log.TokenInputCount = 123; log.TokenOutputCount = 12;
        await f.Db.SaveChangesAsync();
        var inspector = new RunInspectorService(f.Db, f.Service);
        var result = await inspector.ReadAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, f.Ticket.Id, 0, default);
        Assert.Equal(ours, Assert.Single(result.ModelCalls).Id);
        Assert.Equal(123, result.Usage.InputTokens);
        var text = Assert.Single(result.Nodes, x => x.IsStreaming);
        Assert.Equal("First second", text.Text); Assert.Equal($"model:{ours:D}", text.ParentId);
        f.Grants.Allow = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inspector.ReadAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, f.Ticket.Id, 0, default));
    }

    [Fact]
    public async Task InspectorLabelsFallbackAttemptsAndToleratesLegacySettings()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var routed = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var log = await f.Db.AgentRunLogs.SingleAsync(x => x.Id == routed);
        log.InferenceSettingsJson = """{"stage":"Execution","routeAttempt":1}""";
        var legacy = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        (await f.Db.AgentRunLogs.SingleAsync(x => x.Id == legacy)).InferenceSettingsJson = "not json";
        await f.Db.SaveChangesAsync();
        var result = await new RunInspectorService(f.Db, f.Service).ReadAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, f.Ticket.Id, 0, default);
        var call = Assert.Single(result.ModelCalls, x => x.Id == routed);
        Assert.Equal(("Execution", 1), (call.Stage, call.RouteAttempt));
        Assert.Contains("fallback 1 · Execution", Assert.Single(result.Nodes, x => x.Id == $"model:{routed:D}").Label);
        Assert.Equal(0, Assert.Single(result.ModelCalls, x => x.Id == legacy).RouteAttempt);
    }
}
