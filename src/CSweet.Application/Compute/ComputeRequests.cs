using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Application.Compute;

public sealed record RequestComputeEnvironment(Guid WorkstreamId, string DesiredEnvironmentKey,
    string IdempotencyKey, ComputeSpecification Specification);
public sealed record ChangeComputeLifecycle(Guid EnvironmentId, long ExpectedGeneration, string Action, string IdempotencyKey);
public sealed record ComputeEnvironmentView(Guid Id, long Revision, long Generation,
    ComputeDesiredState DesiredState, ComputeLifecycleState State, ComputePersistence Persistence,
    DateTimeOffset CreatedAt, DateTimeOffset LeaseExpiresAt, string? FailureCode, string? DesiredEnvironmentKey = null);
public sealed record ComputeEnvironmentPage(IReadOnlyList<ComputeEnvironmentView> Items, Guid? NextAfterId);
public sealed record RegisteredComputeTemplate(ComputeTemplate Template, string ProviderId, Guid NodeId);

public interface IComputeTemplateCatalog
{
    Task<RegisteredComputeTemplate?> ResolveAsync(Guid organizationId, string templateId, CancellationToken token);
}

public sealed record RequestComputeWorkload(Guid EnvironmentId, long ExpectedGeneration, string IdempotencyKey, ComputeWorkload Workload);
public sealed record ComputeOperationView(Guid Id, Guid EnvironmentId, long Generation, string Status, string? FailureCode, ComputeWorkloadResult? Result);

public interface IComputeBroker
{
    Task<ComputeOperationView> SubmitWorkloadAsync(Guid organizationId, Guid installationId, RequestComputeWorkload request, CancellationToken token);
    Task<ComputeOperationView> ReadOperationAsync(Guid organizationId, Guid installationId, Guid operationId, CancellationToken token);
    Task<ComputeEnvironmentView> RequestAsync(Guid organizationId, Guid installationId,
        RequestComputeEnvironment request, CancellationToken token);
    Task<ComputeEnvironmentView> ChangeLifecycleAsync(Guid organizationId, Guid installationId, ChangeComputeLifecycle request, CancellationToken token);
    Task<ComputeEnvironmentView> ReadAsync(Guid organizationId, Guid installationId, Guid environmentId, CancellationToken token);
    Task<ComputeEnvironmentPage> ListAsync(Guid organizationId, Guid installationId, Guid workstreamId,
        Guid? afterId, int limit, CancellationToken token);
}
