using CSweet.AgentHost.Broker;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentCommunicationOnboardingTests
{
    [Fact]
    public async Task EnsureAsync_CreatesOneProtectedInstanceConversationAndPendingLifecycleEvent()
    {
        await using var db = CreateDb();
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example", Status = OrganizationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var applicationUserId = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            ApplicationUserId = applicationUserId, DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = DateTimeOffset.UtcNow };
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), AgentId = "programmer",
            AgentName = "Programmer", Version = "1.0.0", Status = AgentPackageVersionStatus.Built,
            ManifestJson = "{}", ImportedAt = DateTimeOffset.UtcNow };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
            PackageVersionId = package.Id, PackageVersion = package, BusinessId = organization.Id.ToString("D"),
            IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            AgentInstallationId = installation.Id, DisplayName = "Programmer A", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = DateTimeOffset.UtcNow };
        db.AddRange(organization, owner, package, installation, agent);
        await db.SaveChangesAsync();
        var service = new AgentCommunicationOnboardingService(db);

        var first = await service.EnsureAsync(organization.Id, agent, applicationUserId);
        await db.SaveChangesAsync();
        var second = await service.EnsureAsync(organization.Id, agent, applicationUserId);
        await db.SaveChangesAsync();

        Assert.True(first.Succeeded);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(first.EventId, second.EventId);
        var chat = await db.CoreConversations.Include(x => x.Participants).Include(x => x.Messages).SingleAsync();
        Assert.True(chat.IsDeletionProtected);
        Assert.True(chat.IsPrivate);
        Assert.Equal(agent.Id, chat.AgentOrganizationUserId);
        Assert.Equal(2, chat.Participants.Count);
        Assert.Empty(chat.Messages);
        var lifecycleEvent = await db.AgentOnboardingEventOutbox.SingleAsync();
        Assert.Equal(AgentOnboardingEventOutboxStatus.Pending, lifecycleEvent.Status);
        Assert.Equal(agent.Id, lifecycleEvent.AgentOrganizationUserId);
        Assert.Equal(owner.Id, lifecycleEvent.HiringOrganizationUserId);
        Assert.Equal(chat.Id, lifecycleEvent.ConversationId);
        var payload = AgentOnboardingEventDispatcher.CreatePayload(lifecycleEvent);
        Assert.Equal(lifecycleEvent.OrganizationId, payload.OrganizationId);
    }

    [Fact]
    public async Task EnsureAsync_AfterAgentIsFiredAndInstallationIsRehired_CreatesNewLifecycleEvent()
    {
        await using var db = CreateDb();
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example", Status = OrganizationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var applicationUserId = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            ApplicationUserId = applicationUserId, DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = DateTimeOffset.UtcNow };
        var installationId = Guid.NewGuid();
        var firstAgent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            AgentInstallationId = installationId, DisplayName = "Programmer A", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = DateTimeOffset.UtcNow };
        db.AddRange(organization, owner, firstAgent);
        await db.SaveChangesAsync();
        var service = new AgentCommunicationOnboardingService(db);

        var first = await service.EnsureAsync(organization.Id, firstAgent, applicationUserId);
        await db.SaveChangesAsync();
        firstAgent.IsActive = false;
        firstAgent.ArchivedAt = DateTimeOffset.UtcNow;
        firstAgent.AgentInstallationId = null;
        var secondAgent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            AgentInstallationId = installationId, DisplayName = "Programmer B", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = DateTimeOffset.UtcNow };
        db.CoreOrganizationUsers.Add(secondAgent);
        await db.SaveChangesAsync();

        var second = await service.EnsureAsync(organization.Id, secondAgent, applicationUserId);
        await db.SaveChangesAsync();

        Assert.True(second.Succeeded);
        Assert.NotEqual(first.EventId, second.EventId);
        Assert.NotEqual(first.ConversationId, second.ConversationId);
        var events = await db.AgentOnboardingEventOutbox.OrderBy(x => x.OccurredAt).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(firstAgent.Id, events[0].AgentOrganizationUserId);
        Assert.Equal(secondAgent.Id, events[1].AgentOrganizationUserId);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Pending, events[1].Status);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
