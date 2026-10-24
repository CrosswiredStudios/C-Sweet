using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.UnitTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Product = CSweet.Agent.SoftwareProductManager;

namespace CSweet.DeliveryWorkflow.Tests;

public sealed class ChiefProductContextReceiverTests
{
    [Fact]
    public async Task RealProductManagerReplaysTheSameContextWithoutDuplicatingCeoEffects()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var f = await UserActionServiceTests.ChiefAcceptanceFixtureAsync(db);
        var installation = Guid.NewGuid();
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Hire);
        employee.EmployeeType = EmployeeType.Agent; employee.AgentInstallationId = installation; employee.ReportsToOrganizationUserId = f.Owner;
        await db.SaveChangesAsync();
        var organization = new OrganizationSnapshotResponse(f.Organization, "Active",
            [new(f.Chief, "Chief of Staff", "Agent", null, f.Owner, f.Installation, true),
             new(f.Hire, "Product Manager", "Agent", null, f.Owner, installation, true),
             new(f.Owner, "Owner", "Human", null, null, null, true)], [], [], [], [], DateTimeOffset.UtcNow);
        var audit = new TestAuditEventWriter();
        var hub = new CommunicationHubService(db, audit, new ChatTurnService(db));
        var session = new AgentSession("test", "product", installation.ToString(), f.Organization.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([CommunicationHubCapabilities.Create, CommunicationHubCapabilities.SendMessage]), Revision: 1));
        async Task<JsonElement> Communication(string capability, JsonElement input, CancellationToken token)
        {
            var handler = new CommunicationHubCapabilityHandler(db, hub);
            await foreach (var result in handler.HandleAsync(session, new() { RequestId = Guid.NewGuid().ToString(), Capability = capability,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(input)) }, token))
            {
                Assert.True(result.Succeeded, result.Error);
                return JsonSerializer.Deserialize<JsonElement>(result.Payload.Span);
            }
            throw new InvalidOperationException("Missing communication response.");
        }
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, OrganizationSnapshotResponse>(PlatformCapabilities.OrganizationSnapshotRead, (_, _) => Task.FromResult(organization))
            .RegisterCapability<TeamRosterRequest, TeamRosterResponse>(PlatformCapabilities.TeamRosterRead, (_, _) => Task.FromResult(new TeamRosterResponse(null)))
            .RegisterCapability<JsonElement, JsonElement>(CommunicationHubCapabilities.Create, (r, t) => Communication(CommunicationHubCapabilities.Create, r, t))
            .RegisterCapability<JsonElement, JsonElement>(CommunicationHubCapabilities.SendMessage, (r, t) => Communication(CommunicationHubCapabilities.SendMessage, r, t));
        var context = runtime.CreateContext(f.Organization.ToString(), installation.ToString(),
            new(f.Hire.ToString(), "Product Manager", null, null, null, [], null, null, null));
        var brief = new Product.ProductRoleBriefResponse("Ready", f.Chief, f.Hire, 1, "Deliver the approved customer outcome.",
            ["Validated customer value"], ["Owner-reviewed milestones"], ["Stay within the approved budget"], ["CEO approves team changes"], [], [], DateTimeOffset.UtcNow);
        var input = new Product.ProductContextUpdateRequest(brief, Guid.NewGuid(), "retained-chief-context-delivery");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Recreate the receiver after a simulated sender acknowledgment loss.
            var receiver = new Product.ProductManagerAgent(NullLogger<Product.ProductManagerAgent>.Instance,
                new Product.ProductManagerOrchestrator(NullLogger<Product.ProductManagerOrchestrator>.Instance));
            var result = await receiver.ExecuteCapabilityAsync(new(Guid.NewGuid(), ProductManagementCapabilities.ContextUpdate,
                JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "context-retry", "com.csweet.chief-of-staff"), context, default);
            Assert.True(result.Succeeded, result.Error);
            Assert.True(result.Value!.Value.GetProperty("acknowledged").GetBoolean());
            db.ChangeTracker.Clear();
        }
        var message = Assert.Single(await db.CoreConversationMessages.Where(x => x.SenderOrganizationUserId == f.Hire).ToListAsync());
        Assert.Contains("refreshed", message.Content);
        Assert.False(string.IsNullOrWhiteSpace(message.IdempotencyKey));
        var chat = Assert.Single(await db.CoreConversations.Where(x => x.Participants.Any(p => p.OrganizationUserId == f.Hire) &&
            x.Participants.Any(p => p.OrganizationUserId == f.Owner)).ToListAsync());
        Assert.Equal(chat.Id, message.ConversationId);
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }
}
