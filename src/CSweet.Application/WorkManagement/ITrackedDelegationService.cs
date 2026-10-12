using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public interface ITrackedDelegationService
{
    Task<DelegationSnapshot> CreateAsync(Guid organization, Guid actor, DelegateWithTrackingRequest request, CancellationToken token = default);
    Task<IReadOnlyList<DelegationSnapshot>> ReadAsync(Guid organization, Guid actor, ReadDelegationRequest request, CancellationToken token = default);
    Task<DelegationSnapshot> RespondAsync(Guid organization, Guid actor, DelegationResponseRequest request, CancellationToken token = default);
    Task<DelegationSnapshot> PublishAsync(Guid organization, Guid actor, PublishDelegationReportRequest request, CancellationToken token = default);
    Task<DelegationSnapshot> CancelAsync(Guid organization, Guid actor, CancelDelegationRequest request, CancellationToken token = default);
    Task<WorkTrackingSnapshot> FollowAsync(Guid organization, Guid actor, FollowWorkRequest request, bool follow, CancellationToken token = default);
    Task<WorkTrackingSnapshot> ReadTrackingAsync(Guid organization, Guid actor, Guid item, CancellationToken token = default);
    Task<TrackingUpdate> ProgressAsync(Guid organization, Guid actor, WorkProgressRequest request, CancellationToken token = default);
    Task<IReadOnlyList<PendingTrackingUpdate>> PendingAsync(Guid organization, Guid actor, int limit, CancellationToken token = default);
    Task<WorkTrackingSnapshot> ProcessedAsync(Guid organization, Guid actor, ProcessTrackingUpdateRequest request, CancellationToken token = default);
    Task ReconcileAsync(CancellationToken token = default);
}
