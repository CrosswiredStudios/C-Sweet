using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public sealed record PersonalTodoActor(Guid OrganizationUserId, Guid? AgentInstallationId);

public interface IPersonalTodoService
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
    Task EnsureBoardAsync(Guid organizationId, Guid agentOrganizationUserId,
        CancellationToken cancellationToken = default);
    Task<PersonalTodoDirectory> ListAsync(Guid organizationId, PersonalTodoActor actor,
        CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> AddAsync(Guid organizationId, PersonalTodoActor actor,
        AddPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> ReorderAsync(Guid organizationId, PersonalTodoActor actor,
        ReorderPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> RequeueAsync(Guid organizationId, PersonalTodoActor actor,
        RequeuePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoClaim> ClaimAsync(Guid organizationId, PersonalTodoActor actor,
        ClaimPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> CompleteAsync(Guid organizationId, PersonalTodoActor actor,
        CompletePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> BlockAsync(Guid organizationId, PersonalTodoActor actor,
        BlockPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> ReleaseAsync(Guid organizationId, PersonalTodoActor actor,
        ReleasePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
}
