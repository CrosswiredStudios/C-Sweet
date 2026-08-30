using System.Text.Json;
using CSweet.Application.Notifications;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class ApplicationRealtimeEventTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CommunicationAndNotificationChanges_AreCapturedAndTenantRouted()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var user = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Owner", EmployeeType = EmployeeType.Human, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        var chat = new Conversation
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, InitiatedByOrganizationUserId = user.Id,
            Kind = ConversationKind.Team, Title = "Updates", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        chat.Participants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), OrganizationUserId = user.Id, OrganizationUser = user,
            JoinedAt = DateTimeOffset.UtcNow, Role = ConversationParticipantRole.Coordinator
        });
        var notification = new UserNotification
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, RecipientOrganizationUserId = user.Id,
            Severity = NotificationSeverity.Important, Category = "work", Title = "Review needed",
            Body = "A decision is waiting.", CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(user, chat, notification);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher();

        var count = await new ApplicationRealtimeOutboxDispatcher(db).DispatchBatchAsync(publisher);

        Assert.True(count >= 2);
        Assert.All(publisher.Publications, x => Assert.Contains(user.Id, x.RecipientOrganizationUserIds));
        Assert.Contains(publisher.Publications, x => x.Envelope.EventType == AppRealtimeEvents.NotificationCreated);
        Assert.Contains(publisher.Publications, x => x.Envelope.EventType == "com.csweet.communication.chat.created.v1");
        Assert.All(await db.ApplicationRealtimeOutbox.ToListAsync(), x => Assert.Equal(ApplicationRealtimeOutboxStatus.Published, x.Status));
    }

    [Fact]
    public async Task RecipientSnapshot_IncludesRemovalEventButExcludesFormerMemberFromFutureMessages()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var user = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
            ApplicationUserId = Guid.NewGuid(), DisplayName = "Member", EmployeeType = EmployeeType.Human,
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = organizationId,
            InitiatedByOrganizationUserId = user.Id, Kind = ConversationKind.Team, Title = "Secure",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var participant = new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = chat.Id,
            OrganizationUserId = user.Id, OrganizationUser = user, Role = ConversationParticipantRole.Member,
            JoinedAt = DateTimeOffset.UtcNow };
        db.AddRange(user, chat, participant);
        await db.SaveChangesAsync();

        participant.LeftAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var removal = await db.ApplicationRealtimeOutbox.OrderByDescending(x => x.Sequence)
            .FirstAsync(x => x.EventType == CommunicationEvents.ParticipantRemoved);
        Assert.Contains(user.Id, JsonSerializer.Deserialize<List<Guid>>(removal.RecipientOrganizationUserIdsJson)!);

        db.CoreConversationMessages.Add(new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat.Id,
            SenderOrganizationUserId = null, Role = ConversationRole.Assistant, Content = "Private update",
            CorrelationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var message = await db.ApplicationRealtimeOutbox.OrderByDescending(x => x.Sequence)
            .FirstAsync(x => x.EventType == CommunicationEvents.MessageCreated);
        Assert.DoesNotContain(user.Id, JsonSerializer.Deserialize<List<Guid>>(message.RecipientOrganizationUserIdsJson)!);
    }

    [Fact]
    public async Task EmployeeDirectoryChanges_AreCapturedAndScopedToActiveOrganizationMembers()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var member = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Owner", EmployeeType = EmployeeType.Human, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var otherOrganizationMember = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Other Owner", EmployeeType = EmployeeType.Human, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(member, otherOrganizationMember);
        await db.SaveChangesAsync();
        await new ApplicationRealtimeOutboxDispatcher(db).DispatchBatchAsync(new RecordingPublisher());

        var hire = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = "New Hire",
            EmployeeType = EmployeeType.Agent, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(hire);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher();
        await new ApplicationRealtimeOutboxDispatcher(db).DispatchBatchAsync(publisher);

        var created = Assert.Single(publisher.Publications,
            x => x.Envelope.EventType == AppRealtimeEvents.EmployeeDirectoryChanged);
        var createdData = created.Envelope.Data.Deserialize<EmployeeDirectoryChangedEvent>(JsonOptions);
        Assert.NotNull(createdData);
        Assert.Equal(organizationId, createdData.OrganizationId);
        Assert.Equal(hire.Id, createdData.OrganizationUserId);
        Assert.Equal("Created", createdData.ChangeKind);
        Assert.Contains(member.Id, created.RecipientOrganizationUserIds);
        Assert.Contains(hire.Id, created.RecipientOrganizationUserIds);
        Assert.DoesNotContain(otherOrganizationMember.Id, created.RecipientOrganizationUserIds);

        hire.DisplayName = "Renamed Hire";
        hire.RoleId = Guid.NewGuid();
        await db.SaveChangesAsync();
        publisher = new RecordingPublisher();
        await new ApplicationRealtimeOutboxDispatcher(db).DispatchBatchAsync(publisher);
        Assert.Equal("Updated", Assert.Single(publisher.Publications)
            .Envelope.Data.Deserialize<EmployeeDirectoryChangedEvent>(JsonOptions)!.ChangeKind);

        hire.IsActive = false;
        await db.SaveChangesAsync();
        publisher = new RecordingPublisher();
        await new ApplicationRealtimeOutboxDispatcher(db).DispatchBatchAsync(publisher);
        var deactivated = Assert.Single(publisher.Publications);
        Assert.Equal("Deactivated", deactivated.Envelope.Data.Deserialize<EmployeeDirectoryChangedEvent>(JsonOptions)!.ChangeKind);
        Assert.Contains(member.Id, deactivated.RecipientOrganizationUserIds);
        Assert.DoesNotContain(hire.Id, deactivated.RecipientOrganizationUserIds);

        hire.IsActive = true;
        await db.SaveChangesAsync();
        publisher = new RecordingPublisher();
        await new ApplicationRealtimeOutboxDispatcher(db).DispatchBatchAsync(publisher);
        var activated = Assert.Single(publisher.Publications);
        Assert.Equal("Activated", activated.Envelope.Data.Deserialize<EmployeeDirectoryChangedEvent>(JsonOptions)!.ChangeKind);
        Assert.Contains(hire.Id, activated.RecipientOrganizationUserIds);
    }

    [Fact]
    public async Task GenericProjectEvents_AreLiveRefreshedOnlyToAuthorizedInspectors()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var workstreamId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var manager = User(organizationId, "Manager", OrganizationPermissionLevel.Manager);
        var supervisor = User(organizationId, "Supervisor");
        var teamMember = User(organizationId, "Team member");
        var outsider = User(organizationId, "Outsider");
        db.AddRange(manager, supervisor, teamMember, outsider,
            new Workstream
            {
                Id = workstreamId, OrganizationId = organizationId,
                AccountableManagerOrganizationUserId = manager.Id,
                Name = "Auditable game", Outcome = "Ship it", CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new WorkstreamSupervisionAssignment
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = workstreamId,
                SupervisorOrganizationUserId = supervisor.Id, RoleKey = "creative-director",
                StartsAt = DateTimeOffset.UtcNow
            },
            new WorkstreamTeamAssignmentRecord
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = workstreamId,
                TeamId = teamId, StartsAt = DateTimeOffset.UtcNow
            },
            new TeamMembership
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TeamId = teamId,
                OrganizationUserId = teamMember.Id, SourceType = "Workstream",
                JoinedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        db.ApplicationRealtimeOutbox.RemoveRange(db.ApplicationRealtimeOutbox);
        await db.SaveChangesAsync();

        var context = new W.AgentWorkContext(organizationId, workstreamId, teamId, null, null,
            null, null, Guid.NewGuid(), null, "video-game-production.v2");
        var resourceEvent = new W.GenericResourceEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, context,
            "Build", Guid.NewGuid(), 1, "phaser.web-2d.v1", "Published",
            JsonSerializer.SerializeToElement(new { status = "Published" }));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            EventType = W.WorkstreamEventNames.BuildPublishedV1,
            DataJson = JsonSerializer.Serialize(resourceEvent, JsonOptions),
            IdempotencyKey = Guid.NewGuid().ToString("N"), Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow, OccurredAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var realtime = Assert.Single(db.ApplicationRealtimeOutbox);
        Assert.Equal(W.WorkstreamEventNames.BuildPublishedV1, realtime.EventType);
        var recipients = JsonSerializer.Deserialize<List<Guid>>(realtime.RecipientOrganizationUserIdsJson)!;
        Assert.Contains(manager.Id, recipients);
        Assert.Contains(supervisor.Id, recipients);
        Assert.Contains(teamMember.Id, recipients);
        Assert.DoesNotContain(outsider.Id, recipients);
    }

    [Fact]
    public async Task NonProjectAgentEvents_AreNotMirroredToProjectRealtime()
    {
        await using var db = CreateDb();
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), EventType = "com.example.unrelated.v1",
            DataJson = "{}", IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow, OccurredAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        Assert.Empty(db.ApplicationRealtimeOutbox);
    }

    private static OrganizationUser User(Guid organizationId, string name,
        OrganizationPermissionLevel permission = OrganizationPermissionLevel.Contributor) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = Guid.NewGuid(),
        DisplayName = name, EmployeeType = EmployeeType.Human, PermissionLevel = permission,
        IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingPublisher : IApplicationRealtimePublisher
    {
        public List<ApplicationRealtimePublication> Publications { get; } = [];
        public Task PublishAsync(ApplicationRealtimePublication publication, CancellationToken cancellationToken = default)
        {
            Publications.Add(publication);
            return Task.CompletedTask;
        }
    }
}
