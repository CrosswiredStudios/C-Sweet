using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CommunicationEventTests
{
    [Fact]
    public async Task SaveChanges_CapturesEveryConversationMutationTransactionally()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var chat = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, InitiatedByOrganizationUserId = actorId,
            Kind = ConversationKind.Team, Title = "launch", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var participant = new ConversationParticipant
        {
            Id = Guid.NewGuid(), ConversationId = chat.Id, OrganizationUserId = actorId,
            Role = ConversationParticipantRole.Coordinator, JoinedAt = DateTimeOffset.UtcNow
        };
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = chat.Id, SenderOrganizationUserId = actorId,
            Role = ConversationRole.User, Content = "Ready", CorrelationId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(chat, participant, message);
        await db.SaveChangesAsync();

        AssertEvents(db, CommunicationEvents.ChatCreated, CommunicationEvents.ParticipantAdded,
            CommunicationEvents.MessageCreated);

        participant.LastReadMessageSequence = message.Sequence;
        await db.SaveChangesAsync();
        AssertEvents(db, CommunicationEvents.ReadUpdated);

        chat.Title = "launch-room";
        participant.Role = ConversationParticipantRole.Member;
        message.Content = "Ready to launch";
        await db.SaveChangesAsync();
        AssertEvents(db, CommunicationEvents.ChatUpdated, CommunicationEvents.ParticipantUpdated,
            CommunicationEvents.MessageUpdated);

        participant.LeftAt = DateTimeOffset.UtcNow;
        chat.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        AssertEvents(db, CommunicationEvents.ChatArchived, CommunicationEvents.ParticipantRemoved);

        db.Remove(message);
        db.Remove(chat);
        await db.SaveChangesAsync();
        AssertEvents(db, CommunicationEvents.ChatDeleted, CommunicationEvents.MessageDeleted);

        var eventTypes = await db.CommunicationEventOutbox.Select(x => x.EventType).Distinct().ToListAsync();
        Assert.Equal(CommunicationEvents.All.Order(), eventTypes.Order());
        Assert.All(await db.CommunicationEventOutbox.ToListAsync(), item =>
        {
            Assert.Equal(organizationId, item.OrganizationId);
            Assert.Equal(chat.Id, item.ChatId);
            Assert.Equal(CommunicationEventOutboxStatus.Pending, item.Status);
            Assert.True(JsonDocument.Parse(item.DataJson).RootElement.ValueKind == JsonValueKind.Object);
        });
    }

    [Fact]
    public async Task Dispatcher_TargetsOnlyGrantedSubscribersAndPublishesStableEnvelope()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var subscribed = Installation(CommunicationEvents.MessageCreated);
        var unsubscribed = Installation("some.other.event.v1");
        var wrongOrganization = Installation(CommunicationEvents.MessageCreated);
        db.AgentInstallations.AddRange(subscribed.Installation, unsubscribed.Installation, wrongOrganization.Installation);
        db.AgentInstallationGrants.AddRange(subscribed.Grant, unsubscribed.Grant, wrongOrganization.Grant);
        db.PluginOrganizationGrants.AddRange(
            OrganizationGrant(subscribed.Installation.Id, organizationId),
            OrganizationGrant(unsubscribed.Installation.Id, organizationId),
            OrganizationGrant(wrongOrganization.Installation.Id, Guid.NewGuid()));
        var outbox = new CommunicationEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ChatId = Guid.NewGuid(),
            EventType = CommunicationEvents.MessageCreated,
            Subject = CommunicationEvents.Subject(organizationId, Guid.NewGuid()), DataJson = "{\"content\":\"hello\"}",
            Status = CommunicationEventOutboxStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow,
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.CommunicationEventOutbox.Add(outbox);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher();

        var count = await new CommunicationEventOutboxDispatcher(db).DispatchBatchAsync(publisher);

        Assert.Equal(1, count);
        var publication = Assert.Single(publisher.Publications);
        Assert.Equal(subscribed.Installation.Id, publication.TargetInstallationId);
        Assert.Equal(outbox.Id, publication.Envelope.EventId);
        Assert.Equal(outbox.Sequence, publication.Envelope.Sequence);
        Assert.Equal("hello", publication.Envelope.Data.GetProperty("content").GetString());
        Assert.Equal(CommunicationEventOutboxStatus.Published, outbox.Status);
    }

    private static void AssertEvents(CSweetDbContext db, params string[] expected)
    {
        var actual = db.CommunicationEventOutbox.Local.Select(x => x.EventType).ToList();
        foreach (var eventType in expected) Assert.Contains(eventType, actual);
    }

    private static (AgentInstallation Installation, AgentInstallationGrant Grant) Installation(string subscription)
    {
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), RevisionStatus = PluginRevisionStatus.Active,
            PackageVersionId = Guid.NewGuid(), Scope = PluginInstallationScope.System, BusinessId = "default",
            IsEnabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            EventSubscriptionsJson = JsonSerializer.Serialize(new[] { subscription }),
            ProvidedCapabilitiesJson = "[]", RequiredCapabilitiesJson = "[]",
            NetworkAccessJson = "[]", ResourceLimitsJson = "{}", ApprovedAt = DateTimeOffset.UtcNow
        };
        installation.Grant = grant;
        return (installation, grant);
    }

    private static PluginOrganizationGrant OrganizationGrant(Guid installationId, Guid organizationId) => new()
    {
        Id = Guid.NewGuid(), PluginInstallationId = installationId, OrganizationId = organizationId,
        GrantedAt = DateTimeOffset.UtcNow
    };

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingPublisher : ICommunicationEventPublisher
    {
        public List<CommunicationEventPublication> Publications { get; } = [];
        public Task PublishAsync(CommunicationEventPublication publication, CancellationToken cancellationToken = default)
        {
            Publications.Add(publication);
            return Task.CompletedTask;
        }
    }
}
