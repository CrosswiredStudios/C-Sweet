namespace CSweet.Domain.Core;

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? AgentOrganizationUserId { get; set; }
    public Guid InitiatedByOrganizationUserId { get; set; }
    public ConversationKind Kind { get; set; } = ConversationKind.DirectHumanAgent;
    public Guid? TeamId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsDeletionProtected { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Organization? Organization { get; set; }
    public OrganizationUser? AgentOrganizationUser { get; set; }
    public ICollection<ConversationMessage> Messages { get; set; } = new List<ConversationMessage>();
    public ICollection<ConversationParticipant> Participants { get; set; } = new List<ConversationParticipant>();
}

public enum ConversationKind
{
    DirectHumanAgent,
    AgentChannel,
    Team,
    Project
}

public enum ConversationParticipantRole
{
    Member,
    Coordinator,
    Observer
}

public sealed class ConversationParticipant
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public ConversationParticipantRole Role { get; set; }
    public long LastReadMessageSequence { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public Conversation? Conversation { get; set; }
    public OrganizationUser? OrganizationUser { get; set; }
}
