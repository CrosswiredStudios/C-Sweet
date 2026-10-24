namespace CSweet.Domain.Core;

public sealed class ChiefLeadershipAgenda
{
    public Guid Id { get; set; } // Organization identity; survives Chief replacement.
    public string ProfileKey { get; set; } = "general";
    public int ProfileVersion { get; set; } = 1;
    public long FocusRevision { get; set; }
    public Guid? FocusId { get; set; }
    public bool Paused { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ChiefFocusRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public long Revision { get; set; }
    public string DomainId { get; set; } = "";
    public string? CustomText { get; set; }
    public Guid DecisionMakerId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SourceTurnId { get; set; }
    public Guid? SourceDecisionId { get; set; }
    public Guid? SupersedesId { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
}

public sealed class ChiefCoverageRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string ProfileKey { get; set; } = "";
    public int ProfileVersion { get; set; }
    public string DomainId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; }
    public string CoverageStatus { get; set; } = "Unassessed";
    public string ExecutionStatus { get; set; } = "Backlog";
    public long Revision { get; set; } = 1;
    public Guid? AccountableOwnerId { get; set; }
    public string? CoverageModel { get; set; }
    public string? Rationale { get; set; }
    public string? NextAction { get; set; }
    public Guid? RecommendationId { get; set; }
    public Guid? WorkItemId { get; set; }
    public Guid? DecisionId { get; set; }
    public Guid? AnswerDecisionId { get; set; }
    public string? InputFingerprint { get; set; }
    public string? BlockCode { get; set; }
    public string? AcceptedResultJson { get; set; }
    public DateTimeOffset? ReviewAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Structural purpose and source revision for a native immutable decision.</summary>
public sealed class ChiefFocusPrompt
{
    public Guid Id { get; set; } // ExecutiveDecision.Id
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ConversationId { get; set; }
    public string ProfileKey { get; set; } = "";
    public int ProfileVersion { get; set; }
    public long ExpectedFocusRevision { get; set; }
    public Guid? AssessmentId { get; set; }
    public string Purpose { get; set; } = "Focus";
    public string IdempotencyKey { get; set; } = "";
}

public sealed class ChiefLeadershipReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public string OperationKey { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
