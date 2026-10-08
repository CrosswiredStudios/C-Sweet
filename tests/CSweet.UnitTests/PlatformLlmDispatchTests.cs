using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    [Theory]
    [InlineData("installation")]
    [InlineData("grant")]
    [InlineData("capability")]
    [InlineData("runtime")]
    [InlineData("deadline")]
    [InlineData("employee")]
    [InlineData("provider")]
    [InlineData("endpoint")]
    [InlineData("work")]
    [InlineData("lease")]
    [InlineData("unreceipted-chat")]
    [InlineData("direct-unreceipted-chat")]
    [InlineData("memory-read")]
    public async Task DispatchRechecksAuthorityAfterClientConstruction(string revoked)
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var providerId = await AddProviderAsync(db);
        var session = new AgentSession(Guid.NewGuid().ToString("N"), "test-agent", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>([PlatformCapabilities.LlmChatStream]), 1));
        await SeedDispatchAuthorityAsync(db, session);
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(session.BusinessId),
            AgentInstallationId = Guid.Parse(session.InstallationId), EmployeeType = EmployeeType.Agent, IsActive = true };
        db.CoreOrganizationUsers.Add(employee);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = Guid.Parse(session.InstallationId),
            OrganizationId = session.BusinessId, Status = AgentWorkStatus.Leased, DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId),
            Attempt = 1, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        db.AgentWorkItems.Add(work); db.AgentWorkAttempts.Add(attempt); await db.SaveChangesAsync();
        var client = new StreamingChatClient();
        var provider = new ChangingDispatchFactory(client, async () =>
        {
            await using var changed = new CSweetDbContext(options);
            if (revoked == "installation") (await changed.AgentInstallations.SingleAsync()).IsEnabled = false;
            if (revoked == "grant") (await changed.AgentInstallationGrants.SingleAsync()).GrantRevision++;
            if (revoked == "capability") (await changed.AgentInstallationGrants.SingleAsync()).RequiredCapabilitiesJson = "[]";
            if (revoked == "runtime") (await changed.AgentRuntimeInstances.SingleAsync()).TransitionTo(AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
            if (revoked == "deadline") (await changed.AgentRuntimeInstances.SingleAsync()).RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (revoked == "employee") (await changed.CoreOrganizationUsers.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow;
            if (revoked == "provider") (await changed.LlmProviderProfiles.SingleAsync()).IsEnabled = false;
            if (revoked == "endpoint") (await changed.LlmProviderProfiles.SingleAsync()).BaseUrl = "https://changed.invalid/";
            if (revoked == "work") (await changed.AgentWorkItems.SingleAsync()).Status = AgentWorkStatus.Cancelled;
            if (revoked == "lease") (await changed.AgentWorkAttempts.SingleAsync()).LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (revoked is "unreceipted-chat" or "direct-unreceipted-chat") (await changed.AgentWorkItems.SingleAsync()).SourceType = "chat-turn";
            if (revoked == "memory-read") changed.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(),
                OrganizationId = Guid.Parse(session.BusinessId), EmployeeId = employee.Id, InstallationId = Guid.Parse(session.InstallationId),
                RuntimeId = Guid.Parse(session.RuntimeInstanceId), WorkId = work.Id, Attempt = 1, GrantRevision = 1,
                Capability = CSweet.Memory.CSweetMemoryCapabilities.Query, EvidenceJson = "{}", ReceiptHash = "fixture" });
            await changed.SaveChangesAsync();
        });
        var handler = new PlatformLlmCapabilityHandler(db, provider, new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], new TestMediaAssetService(),
            NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString(), Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(new { providerProfileId = providerId, messages = new[] { new { role = "user", text = "Private memory" } } }) };
        using var attribution = revoked == "direct-unreceipted-chat" ? null : new InferenceExecutionAttribution(work.Id, Guid.NewGuid(), 1).Enter();
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.StreamAsync(session, request, default)) results.Add(result);
        var failure = Assert.Single(results); Assert.False(failure.Succeeded); Assert.False(failure.Retryable);
        Assert.Equal("llm.dispatch_denied", failure.FailureCode); Assert.Null(client.ReceivedOptions);
        var log = Assert.Single(await db.AgentRunLogs.ToListAsync()); Assert.Equal("Denied", log.Status); Assert.Null(log.ProviderStartedAt);
        Assert.DoesNotContain("Private memory", failure.Error);
    }

    private sealed class ChangingDispatchFactory(IChatClient client, Func<Task> change) : ILlmProviderFactory
    {
        public async Task<IChatClient> CreateChatClientAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
        { await change(); return client; }
    }
}
