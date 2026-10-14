using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

public interface IAppRealtimeConnection
{
    event Action<AppRealtimeEventEnvelope>? EventReceived;
    event Action? Reconnected;
    Task StartAsync(CancellationToken cancellationToken = default);
}
