using CSweet.Application.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>
/// Compatibility surface for legacy personal-to-do HTTP and MCP contracts. It owns no persistence;
/// every operation delegates to the canonical work-item mutation engine.
/// </summary>
public sealed class PersonalTodoService : IPersonalTodoService
{
    private readonly IWorkItemMutationEngine _engine;

    public PersonalTodoService(IWorkItemMutationEngine engine) => _engine = engine;
    public PersonalTodoService(CSweet.Infrastructure.Persistence.CSweetDbContext db, TimeProvider clock)
        : this(new WorkItemMutationEngine(db, clock)) { }

    public Task ReconcileAsync(CancellationToken cancellationToken = default) => _engine.ReconcileAsync(cancellationToken);
    public Task EnsureBoardAsync(Guid organizationId, Guid ownerOrganizationUserId, CancellationToken cancellationToken = default) => _engine.EnsureBoardAsync(organizationId, ownerOrganizationUserId, cancellationToken);
    public Task<Wire.PersonalTodoDirectory> ListAsync(Guid organizationId, PersonalTodoActor actor, bool includeArchived = false, CancellationToken cancellationToken = default) => _engine.ListAsync(organizationId, actor, includeArchived, cancellationToken);
    public Task<Wire.PersonalTodoItem> AddAsync(Guid organizationId, PersonalTodoActor actor, Wire.AddPersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.AddAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> ReorderAsync(Guid organizationId, PersonalTodoActor actor, Wire.ReorderPersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.ReorderAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> RequeueAsync(Guid organizationId, PersonalTodoActor actor, Wire.RequeuePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.RequeueAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> ActivateAsync(Guid organizationId, PersonalTodoActor actor, Wire.ActivatePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.ActivateAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> UpdateAsync(Guid organizationId, PersonalTodoActor actor, Wire.UpdatePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.UpdateAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> ArchiveAsync(Guid organizationId, PersonalTodoActor actor, Wire.ArchivePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.ArchiveAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> RestoreAsync(Guid organizationId, PersonalTodoActor actor, Wire.RestorePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.RestoreAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> SetHumanStatusAsync(Guid organizationId, PersonalTodoActor actor, Wire.SetHumanPersonalTodoStatusRequest request, CancellationToken cancellationToken = default) => _engine.SetHumanStatusAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoClaim> ClaimAsync(Guid organizationId, PersonalTodoActor actor, Wire.ClaimPersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.ClaimAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> CompleteAsync(Guid organizationId, PersonalTodoActor actor, Wire.CompletePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.CompleteAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> BlockAsync(Guid organizationId, PersonalTodoActor actor, Wire.BlockPersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.BlockAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> ReleaseAsync(Guid organizationId, PersonalTodoActor actor, Wire.ReleasePersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.ReleaseAsync(organizationId, actor, request, cancellationToken);
    public Task<Wire.PersonalTodoItem> DeferAsync(Guid organizationId, PersonalTodoActor actor, Wire.DeferPersonalTodoItemRequest request, CancellationToken cancellationToken = default) => _engine.DeferAsync(organizationId, actor, request, cancellationToken);
}
