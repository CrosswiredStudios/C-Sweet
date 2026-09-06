namespace CSweet.Contracts.SourceControl;

public sealed record SourceControlActivityPage(IReadOnlyList<SourceControlActivityEntry> Items, long? NextBeforeSequence);
public sealed record SourceControlActivityEntry(Guid Id, long Sequence, DateTimeOffset OccurredAt, string EventType,
    string Outcome, string EntityType, Guid? EntityId, string ActorKind, string? ActorDisplayName,
    Guid? ActorApplicationUserId, Guid? ActorInstallationId, Guid TraceId);
