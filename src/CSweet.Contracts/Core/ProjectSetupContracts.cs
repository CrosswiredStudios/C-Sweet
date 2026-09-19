namespace CSweet.Contracts.Core;

public sealed record CreateProjectRequest(string Name, string Goal, Guid ManagerId, Guid? TeamId,
    IReadOnlyList<Guid> MemberIds, Guid? RepositoryId, Guid? IntakeId, long? IntakeRevision, string IdempotencyKey);
public sealed record UpdateProjectMembersRequest(Guid ManagerId, IReadOnlyList<Guid> MemberIds, long ExpectedRevision,
    Guid? IntakeId = null, long? IntakeRevision = null);
public sealed record ProjectSetupPerson(Guid Id, string Name, string EmployeeType, Guid? TeamId, bool CanManageProject);
public sealed record ProjectSetupTeam(Guid Id, string Name);
public sealed record ProjectSetupRepository(Guid Id, string Name);
public sealed record ProjectSetupOptions(Guid CurrentUserId, bool CanCreate, IReadOnlyList<ProjectSetupPerson> People,
    IReadOnlyList<ProjectSetupTeam> Teams, IReadOnlyList<ProjectSetupRepository> Repositories);
public sealed record ProjectSetupDraft(Guid Id, string Name, string Goal, Guid ManagerId, Guid DeveloperId,
    Guid? TeamId, Guid? ProjectId, long Revision, string Status);
public sealed record ProjectSetupResult(Guid ProjectId, Guid BoardId, long Revision);
public sealed record ProjectMembersDetail(Guid ProjectId, string Name, Guid ManagerId, Guid TeamId,
    IReadOnlyList<Guid> MemberIds, long Revision);

public sealed record ChangeProjectStatusRequest(string Status, long ExpectedRevision);
