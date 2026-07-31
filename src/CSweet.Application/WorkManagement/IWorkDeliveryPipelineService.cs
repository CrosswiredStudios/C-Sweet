using CSweet.Contracts.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public interface IWorkDeliveryPipelineService
{
    Task<DeliveryPipelineResponse?> GetAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<DeliveryPipelineResponse> ConfigureAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        ConfigureDeliveryPipelineRequest request,
        CancellationToken cancellationToken = default);

    Task<DeliveryPipelineResponse> ChangeStateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        string action,
        ChangeDeliveryPipelineStateRequest request,
        CancellationToken cancellationToken = default);

    Task<int> PulseAsync(CancellationToken cancellationToken = default);

    Task<bool> RoutePublishedDevelopmentAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid developerInstallationId,
        long expectedItemRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<QualityRunResult> SubmitQualityAsync(
        Guid organizationId,
        Guid qualityInstallationId,
        SubmitQualityResultRequest request,
        CancellationToken cancellationToken = default);
}
