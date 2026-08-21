using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public sealed record PersonalTodoActor(Guid OrganizationUserId, Guid? AgentInstallationId);

public interface IPersonalTodoService
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
    Task EnsureBoardAsync(Guid organizationId, Guid ownerOrganizationUserId,
        CancellationToken cancellationToken = default);
    Task<PersonalTodoDirectory> ListAsync(Guid organizationId, PersonalTodoActor actor,
        bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> AddAsync(Guid organizationId, PersonalTodoActor actor,
        AddPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> ReorderAsync(Guid organizationId, PersonalTodoActor actor,
        ReorderPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> RequeueAsync(Guid organizationId, PersonalTodoActor actor,
        RequeuePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> ActivateAsync(Guid organizationId, PersonalTodoActor actor,
        ActivatePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> UpdateAsync(Guid organizationId, PersonalTodoActor actor,
        UpdatePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> ArchiveAsync(Guid organizationId, PersonalTodoActor actor,
        ArchivePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> RestoreAsync(Guid organizationId, PersonalTodoActor actor,
        RestorePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> SetHumanStatusAsync(Guid organizationId, PersonalTodoActor actor,
        SetHumanPersonalTodoStatusRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoClaim> ClaimAsync(Guid organizationId, PersonalTodoActor actor,
        ClaimPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> CompleteAsync(Guid organizationId, PersonalTodoActor actor,
        CompletePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> BlockAsync(Guid organizationId, PersonalTodoActor actor,
        BlockPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> ReleaseAsync(Guid organizationId, PersonalTodoActor actor,
        ReleasePersonalTodoItemRequest request, CancellationToken cancellationToken = default);
    Task<PersonalTodoItem> DeferAsync(Guid organizationId, PersonalTodoActor actor,
        DeferPersonalTodoItemRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical mutation engine behind personal-board compatibility APIs and agent MCP tools.
/// Callers should depend on the narrower board-facing services rather than persist work items directly.
/// </summary>
public interface IWorkItemMutationEngine : IPersonalTodoService;
