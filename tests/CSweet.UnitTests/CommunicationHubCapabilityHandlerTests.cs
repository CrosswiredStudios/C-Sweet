using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CommunicationHubCapabilityHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReadChat_ReturnsObjectRootWithMessages()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Example",
            Status = OrganizationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var installationId = Guid.NewGuid();
        var manager = User(organization.Id, "Manager", EmployeeType.Human);
        var agent = User(organization.Id, "Product Manager", EmployeeType.Agent);
        agent.AgentInstallationId = installationId;
        db.AddRange(organization, manager, agent);
        await db.SaveChangesAsync();

        var hub = new CommunicationHubService(db, new TestAuditEventWriter(), new ChatTurnService(db));
        var created = await hub.CreateAsync(
            organization.Id,
            manager.Id,
            new CreateCommunicationChatRequest(null, null, true, true, [agent.Id]));
        Assert.True(created.Succeeded);
        await hub.SendAsync(
            organization.Id,
            created.Chat!.Id,
            manager.Id,
            new SendCommunicationMessageRequest("Build a match-three game."));

        var handler = new CommunicationHubCapabilityHandler(db, hub);
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"),
            "com.csweet.product-manager",
            installationId.ToString("D"),
            organization.Id.ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(
                new HashSet<string>(),
                new HashSet<string>(),
                new HashSet<string>([CommunicationHubCapabilities.Read], StringComparer.Ordinal),
                Revision: 1));
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = CommunicationHubCapabilities.Read,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(
                new { chatId = created.Chat.Id },
                JsonOptions))
        };

        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, CancellationToken.None))
            results.Add(result);

        var response = Assert.Single(results);
        Assert.True(response.Succeeded, response.Error);
        using var payload = JsonDocument.Parse(response.Payload.ToByteArray());
        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal("Build a match-three game.", Assert.Single(messages.EnumerateArray()).GetProperty("content").GetString());
    }

    private static OrganizationUser User(Guid organizationId, string name, EmployeeType employeeType) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        DisplayName = name,
        EmployeeType = employeeType,
        PermissionLevel = OrganizationPermissionLevel.Manager,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
