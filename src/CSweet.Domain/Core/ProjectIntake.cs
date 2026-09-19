namespace CSweet.Domain.Core;

public sealed class ProjectIntake
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestingHumanId { get; set; }
    public Guid DeveloperId { get; set; }
    public Guid DeveloperInstallationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SourceMessageId { get; set; }
    public Guid? SourceChatTurnId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? BoardId { get; set; }
    public Guid? RootItemId { get; set; }
    public Guid? PendingWorkItemId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? ManagerId { get; set; }
    public Guid? ChiefId { get; set; }
    public Guid? CoordinationSessionId { get; set; }
    public Guid? HiringRecommendationId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public string OriginalRequest { get; set; } = "";
    public string Name { get; set; } = "";
    public string Goal { get; set; } = "";
    public string TicketOwner { get; set; } = "ask";
    public string Status { get; set; } = "AwaitingProjectChoice";
    public string? Issue { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public Guid? LastChoiceMessageId { get; set; }
    public string? LastChoiceKey { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectParticipant
{
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public Guid AddedByOrganizationUserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class ProjectDeliveryBinding
{
    public Guid WorkstreamId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BoardId { get; set; }
    public Guid TeamId { get; set; }
    public Guid? RepositoryId { get; set; }
    public string CreationKey { get; set; } = "";
    public long Revision { get; set; } = 1;
}

/// <summary>One concurrent project reservation per agent manager; humans do not occupy this table.</summary>
public sealed class ProjectManagerReservation
{
    public Guid OrganizationUserId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? IntakeId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public long Revision { get; set; } = 1;
}

/// <summary>Rollout snapshot, never inferred from work started after enforcement is enabled.</summary>
public sealed class LegacyDevelopmentAuthorization
{
    public Guid WorkItemId { get; set; }
    public Guid OrganizationId { get; set; }
}

/// <summary>Existing environments for in-flight delivery at rollout; cannot authorize new provisioning.</summary>
public sealed class LegacyProjectComputeAuthorization
{
    public Guid EnvironmentId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
}
