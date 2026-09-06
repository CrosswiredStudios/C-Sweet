namespace CSweet.Contracts.SourceControl;

public sealed record SourceControlConnectionDetails(Guid Id, string Name, string Provider, string Mode, string Status,
    string AccountLogin, string AccountType, long Revision, DateTimeOffset? LastVerifiedAt, int RepositoryCount,
    int ActiveWorkspaceCount, int TemplateCount, bool IsBusinessDefault);
public sealed record RenameSourceControlConnectionRequest(string Name, long ExpectedRevision);
public sealed record SourceControlConnectionHealth(bool Available, string Scope, string Message, DateTimeOffset CheckedAt);

public sealed record SourceControlConnectionDisconnectPlan(bool CanDisconnect, IReadOnlyList<string> Blockers,
    bool CanDisconnectWithDependencies = false, IReadOnlyList<string>? DependencyBlockers = null);
public sealed record DisconnectSourceControlConnectionRequest(string ConfirmName, long ExpectedRevision, bool SuspendDependentAccess = false);
