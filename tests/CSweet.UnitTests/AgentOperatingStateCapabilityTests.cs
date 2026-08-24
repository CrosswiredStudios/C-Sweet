using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentOperatingStateCapabilityTests
{
    [Fact]
    public async Task Write_IsScopedRevisionSafeAndIdempotent()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter(),
            new PluginStandingPolicyService(db, new TestAuditEventWriter()), new ConversationService(db));
        var session = Session(organizationId, installationId);
        var reviewId = Guid.NewGuid();
        var write = new AgentOperatingStateWriteRequest(
            "product-manager.assessment", "com.csweet.pm.assessment", 1, "Degraded",
            new Dictionary<string, string> { ["team"] = "4" }, ["role-missing"], "fingerprint-1",
            ["staffing-replenishment:1"], reviewId,
            JsonSerializer.SerializeToElement(new { teamHealth = "Deficient" }), null, "review-1");

        var first = await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, write);
        var replay = await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, write);
        Assert.True(first.Succeeded, first.Error);
        Assert.True(replay.Succeeded, replay.Error);
        var firstState = JsonSerializer.Deserialize<AgentOperatingStateResponse>(first.Payload.ToByteArray(), JsonOptions)!;
        var replayState = JsonSerializer.Deserialize<AgentOperatingStateResponse>(replay.Payload.ToByteArray(), JsonOptions)!;
        Assert.Equal(1, firstState.Revision);
        Assert.Equal(firstState.Id, replayState.Id);
        Assert.Single(db.PluginOperationalStates);

        var conflict = write with { ExpectedRevision = 0, IdempotencyKey = "review-2", DecisionFingerprint = "fingerprint-2" };
        var rejected = await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateWrite, conflict);
        Assert.False(rejected.Succeeded);
        Assert.Equal(PlatformCapabilityErrorCode.Conflict.ToString(), rejected.FailureCode);

        var read = await InvokeAsync(handler, session, PlatformCapabilities.AgentOperatingStateRead,
            new AgentOperatingStateReadRequest("product-manager.assessment"));
        var response = JsonSerializer.Deserialize<AgentOperatingStateReadResponse>(read.Payload.ToByteArray(), JsonOptions)!;
        Assert.Equal(reviewId, response.State!.AttentionReviewId);
        Assert.Equal("role-missing", Assert.Single(response.State.ConditionCodes));
    }

    private static AgentSession Session(Guid organizationId, Guid installationId) =>
        new("session", "pm", installationId.ToString("D"), organizationId.ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([
                    PlatformCapabilities.AgentOperatingStateRead,
                    PlatformCapabilities.AgentOperatingStateWrite
                ]), 1));

    private static async Task<CapabilityResult> InvokeAsync(
        PluginOperationsCapabilityHandler handler,
        AgentSession session,
        string capability,
        object payload)
    {
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"), Capability = capability,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
        };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, CancellationToken.None))
            results.Add(result);
        return Assert.Single(results);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
