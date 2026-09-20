using CSweet.Communications.Abstractions;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CommunicationRouterTests
{
    [Fact]
    public async Task DirectMessageWithoutSelection_DoesNotChooseAnAgent()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        db.CommunicationConnections.Add(new CommunicationConnection
        {
            Id = connectionId, OrganizationId = organizationId, ProviderKey = CommunicationProviderKeys.Discord,
            WorkspaceExternalId = "123", WorkspaceMode = CommunicationWorkspaceMode.Contained,
            Status = CommunicationConnectionStatus.Connected, PluginInstallationId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        var humanId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = humanId, OrganizationId = organizationId, DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        });
        var firstAgent = CreateAgent(organizationId, "One");
        var secondAgent = CreateAgent(organizationId, "Two");
        db.CoreOrganizationUsers.AddRange(firstAgent, secondAgent);
        db.ExternalIdentityLinks.Add(new ExternalIdentityLink
        {
            Id = Guid.NewGuid(), ConnectionId = connectionId, OrganizationId = organizationId,
            OrganizationUserId = humanId, ApplicationUserId = Guid.NewGuid(), ExternalUserId = "discord-user",
            IsVerified = true, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var router = new CommunicationRouter(db, new ChatTurnService(db));
        var result = await router.RouteInboundAsync(Envelope(isDirect: true));

        Assert.False(result.Succeeded);
        Assert.Equal("agent_selection_required", result.ErrorCode);
        Assert.Empty(await db.ChatTurns.ToListAsync());
        Assert.Empty(await db.CoreConversations.ToListAsync());

        var selected = await router.RouteInboundAsync(Envelope(isDirect: true, content: "/talk employee:Two"));
        Assert.True(selected.Succeeded);
        Assert.Equal(secondAgent.Id, (await db.ExternalIdentityLinks.SingleAsync()).ActiveDirectAgentOrganizationUserId);
        Assert.Empty(await db.ChatTurns.ToListAsync());

        var cleared = await router.RouteInboundAsync(Envelope(isDirect: true, content: "/talk clear"));
        Assert.True(cleared.Succeeded);
        Assert.Null((await db.ExternalIdentityLinks.SingleAsync()).ActiveDirectAgentOrganizationUserId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AutomatedMessages_NeverReenterRouter(bool isBot, bool isWebhook)
    {
        await using var db = CreateDb();
        var router = new CommunicationRouter(db, new ChatTurnService(db));
        var result = await router.RouteInboundAsync(Envelope(isBot: isBot, isWebhook: isWebhook));
        Assert.True(result.Succeeded);
        Assert.Empty(await db.ChatTurns.ToListAsync());
    }

    [Fact]
    public void CoreProjects_DoNotReferenceTheDiscordImplementation()
    {
        var root = FindRepositoryRoot();
        var forbidden = new[]
        {
            "src/CSweet.Domain/CSweet.Domain.csproj",
            "src/CSweet.Application/CSweet.Application.csproj",
            "src/CSweet.Api/CSweet.Api.csproj",
            "src/CSweet.AgentHost/CSweet.AgentHost.csproj"
        };
        foreach (var relativePath in forbidden)
            Assert.DoesNotContain("CSweet.Communications.Discord", File.ReadAllText(Path.Combine(root, relativePath)), StringComparison.OrdinalIgnoreCase);

        var appHost = File.ReadAllText(Path.Combine(root, "src/CSweet.AppHost/Program.cs"));
        Assert.DoesNotContain("discord-relay", appHost, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relay-postgres", appHost, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DiscordRelay", appHost, StringComparison.OrdinalIgnoreCase);
    }

    private static NormalizedCommunicationEnvelope Envelope(bool isDirect = false, bool isBot = false, bool isWebhook = false, string content = "hello") => new(
        Guid.NewGuid(), "Discord", CommunicationEnvelopeKind.Message, "123", isDirect ? null : "456", null,
        "discord-user", Guid.NewGuid().ToString("N"), null, content, [], isBot, isWebhook,
        DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), new Dictionary<string, string> { ["isDirect"] = isDirect.ToString() });

    private static OrganizationUser CreateAgent(Guid organizationId, string name) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = name, EmployeeType = EmployeeType.Agent,
        PermissionLevel = OrganizationPermissionLevel.Viewer, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
