using CSweet.Contracts.Realtime;

namespace CSweet.Application.Notifications;

public sealed record ApplicationRealtimePublication(
    AppRealtimeEventEnvelope Envelope,
    IReadOnlyList<Guid> RecipientOrganizationUserIds)
{
    public Guid? RecipientApplicationUserId { get; init; }
}

public interface IApplicationRealtimePublisher
{
    Task PublishAsync(ApplicationRealtimePublication publication, CancellationToken cancellationToken = default);
}

public interface IApplicationRealtimeOutboxDispatcher
{
    Task<int> DispatchBatchAsync(IApplicationRealtimePublisher publisher, int batchSize = 100,
        CancellationToken cancellationToken = default);
}
