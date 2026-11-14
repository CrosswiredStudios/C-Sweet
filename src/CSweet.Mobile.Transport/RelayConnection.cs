using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Mobile.Transport;

/// <summary>Callbacks from the relay.</summary>
public interface IRelayHandler
{
    Task OnChannelOpenedAsync(string channelId, string phoneKeyThumbprint);
    Task OnFrameAsync(string channelId, byte[] data);
    Task OnChannelClosedAsync(string channelId, string reason);
    Task OnDetachedAsync(string reason);
    Task OnReconnectedAsync();
    /// <summary>The relay connection ended for good (automatic reconnect gave up or was not enabled).</summary>
    Task OnClosedAsync(Exception? error);
}

/// <summary>One connection to the mobile relay hub. Abstracted so the TLS and stream logic can be tested without SignalR.</summary>
public interface IRelayConnection : IAsyncDisposable
{
    void SetHandler(IRelayHandler handler);
    Task StartAsync(CancellationToken ct);
    Task<string> ChallengeAsync(CancellationToken ct);
    Task AttachHeadquartersAsync(string routeId, string signature, CancellationToken ct);
    Task<string> OpenChannelAsync(string routeId, string ticket, string phoneKey, string signature, CancellationToken ct);
    /// <summary>Sends without waiting for the relay's reply; completes when written. Order is preserved.</summary>
    Task SendFrameAsync(string channelId, byte[] data, CancellationToken ct);
    Task CloseChannelAsync(string channelId);
}

/// <summary>The relay over SignalR: WebSockets only, MessagePack.</summary>
public sealed class SignalRRelayConnection : IRelayConnection
{
    private readonly HubConnection _hub;
    private IRelayHandler? _handler;

    public SignalRRelayConnection(Uri relayOrigin, Action<HttpConnectionOptions>? configure = null, bool reconnect = false)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(relayOrigin, RelayProtocol.HubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                configure?.Invoke(options);
            })
            .AddMessagePackProtocol();
        if (reconnect) builder.WithAutomaticReconnect();
        _hub = builder.Build();
        _hub.On<string, string>("ChannelOpened", (id, thumbprint) => Handler.OnChannelOpenedAsync(id, thumbprint));
        _hub.On<string, byte[]>("Frame", (id, data) => Handler.OnFrameAsync(id, data));
        _hub.On<string, string>("ChannelClosed", (id, reason) => Handler.OnChannelClosedAsync(id, reason));
        _hub.On<string>("Detached", reason => Handler.OnDetachedAsync(reason));
        _hub.Reconnected += _ => Handler.OnReconnectedAsync();
        _hub.Closed += error => _handler?.OnClosedAsync(error) ?? Task.CompletedTask;
    }

    private IRelayHandler Handler => _handler ?? throw new InvalidOperationException("Set a relay handler before starting.");

    public void SetHandler(IRelayHandler handler) => _handler = handler;
    public Task StartAsync(CancellationToken ct) => _hub.StartAsync(ct);
    public Task<string> ChallengeAsync(CancellationToken ct) => _hub.InvokeAsync<string>("Challenge", ct);
    public Task AttachHeadquartersAsync(string routeId, string signature, CancellationToken ct) => _hub.InvokeAsync("AttachHeadquarters", routeId, signature, ct);
    public Task<string> OpenChannelAsync(string routeId, string ticket, string phoneKey, string signature, CancellationToken ct) =>
        _hub.InvokeAsync<string>("OpenChannel", routeId, ticket, phoneKey, signature, ct);
    public Task SendFrameAsync(string channelId, byte[] data, CancellationToken ct) => _hub.SendAsync("SendFrame", channelId, data, ct);
    public Task CloseChannelAsync(string channelId) => _hub.State == HubConnectionState.Connected ? _hub.SendAsync("CloseChannel", channelId) : Task.CompletedTask;
    public ValueTask DisposeAsync() => _hub.DisposeAsync();
}
