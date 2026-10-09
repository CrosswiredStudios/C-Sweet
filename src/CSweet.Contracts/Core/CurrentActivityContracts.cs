namespace CSweet.Contracts.Core;

public sealed record CurrentActivityPage(DateTimeOffset CheckedAt, int Total,
    IReadOnlyList<CurrentActivityItem> Items, int? NextOffset);

public sealed record CurrentActivityStep(string Key, string Kind, string Text, DateTimeOffset OccurredAt);

public sealed record CurrentActivityItem(string Key, Guid? WorkItemId, Guid? BoardId, Guid? AgentWorkItemId,
    Guid? AttemptId, int Attempt, Guid EmployeeId, string EmployeeName, string Title, string? Identifier,
    string Category, string Context, string State, string CurrentAction, DateTimeOffset StartedAt,
    DateTimeOffset? LastProgressAt, DateTimeOffset? LeaseExpiresAt, string? Provider, string? Model,
    bool CanInspect, IReadOnlyList<CurrentActivityStep> PreviousSteps, Guid? ModelRunId = null,
    string ActivityKind = "Work", string? EmployeeRole = null, CurrentActivityParticipant? Collaborator = null,
    string? TechnicalName = null, Guid? PersonalBoardOwnerId = null);

public sealed record CurrentActivityParticipant(Guid EmployeeId, string EmployeeName, string? Role);

// Sequence is the durable audit watermark. Append entries are chunks of the same stream key,
// not independent steps; clients deduplicate Id before appending Text to Key.
public sealed record CurrentActivityFeedEntry(Guid Id, long Sequence, string Key, string Kind, string Label,
    string Text, DateTimeOffset OccurredAt, bool Append = false, long? StreamSequence = null);

public sealed record CurrentActivityFeedPage(IReadOnlyList<CurrentActivityFeedEntry> Entries,
    long NextSequence, bool HasMore, bool EarlierEntriesOmitted, bool EvidenceUnavailable);
