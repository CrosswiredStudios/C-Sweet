using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public interface IWorkDeliveryService
{
    Task<IReadOnlyList<WorkDeliveryPlanResponse>> ReadAsync(Guid organizationId, Guid actorId, ReadWorkDeliveryPlansRequest request, CancellationToken ct = default);
    Task<WorkDeliveryPlanResponse> ConfigureAsync(Guid organizationId, Guid actorId, ConfigureWorkDeliveryPlanRequest request, CancellationToken ct = default);
    Task<WorkDeliveryPlanResponse> ControlAsync(Guid organizationId, Guid actorId, ControlWorkDeliveryPlanRequest request, CancellationToken ct = default);
    Task<WorkDeliveryPlanResponse> AcceptAsync(Guid organizationId, Guid actorId, DecideWorkDeliveryAcceptanceRequest request, CancellationToken ct = default);
    Task<WorkDeliveryPlanResponse> RecoverAsync(Guid organizationId, Guid actorId, RecoverWorkDeliveryRequest request, CancellationToken ct = default);
    Task PulseAsync(CancellationToken ct = default);
    Task<WorkDeliveryEvidenceResponse> ReadEvidenceAsync(Guid organizationId, Guid actorId, ReadWorkDeliveryEvidenceRequest request, CancellationToken ct = default);
    Task<WorkDeliveryPlanResponse> CompleteReviewAsync(Guid organizationId, Guid actorId, CompleteWorkDeliveryReviewRequest request, CancellationToken ct = default);
    Task<WorkDeliveryPlanResponse> FinalizeTaskAsync(Guid organizationId, Guid actorId, FinalizeWorkItemDeliveryRequest request, CancellationToken ct = default);
}
