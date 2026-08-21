using CSweet.Contracts.Realtime;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.AspNetCore.SignalR.Client;

namespace CSweet.UI.Services;

public sealed class AppRealtimeState(HttpClient http) : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _perspectiveLock = new(1, 1);
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = [];
    private HubConnection? _connection;
    private Guid? _perspectiveOrganizationId;
    private Guid? _perspectiveAgentOrganizationUserId;

    public event Action<AppRealtimeEventEnvelope>? EventReceived;
    public event Action? Connected;
    public event Action? Reconnected;
    public event Action? Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null) return;
            var uri = new Uri(http.BaseAddress!, "hubs/app-events");
            _connection = new HubConnectionBuilder()
                .WithUrl(uri, options =>
                {
                    options.HttpMessageHandlerFactory = inner => new BrowserCredentialsHandler { InnerHandler = inner };
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)])
                .Build();
            _connection.On<AppRealtimeEventEnvelope>("AppEvent", Receive);
            _connection.Reconnected += async _ =>
            {
                await RestoreCommunicationPerspectiveAsync();
                Reconnected?.Invoke();
            };
            _connection.Closed += _ => { _connection = null; Disconnected?.Invoke(); return Task.CompletedTask; };
            try
            {
                await _connection.StartAsync(cancellationToken);
                await RestoreCommunicationPerspectiveAsync(cancellationToken);
                Connected?.Invoke();
            }
            catch
            {
                var failed = _connection;
                _connection = null;
                if (failed is not null) await failed.DisposeAsync();
                throw;
            }
        }
        finally { _startLock.Release(); }
    }

    public async Task SetCommunicationPerspectiveAsync(
        Guid? organizationId,
        Guid? agentOrganizationUserId,
        CancellationToken cancellationToken = default)
    {
        if (agentOrganizationUserId.HasValue && !organizationId.HasValue)
            throw new ArgumentException("An organization is required for an agent communication perspective.", nameof(organizationId));

        await _perspectiveLock.WaitAsync(cancellationToken);
        try
        {
            _perspectiveOrganizationId = organizationId;
            _perspectiveAgentOrganizationUserId = agentOrganizationUserId;
            await ApplyCommunicationPerspectiveAsync(_connection, cancellationToken);
        }
        finally { _perspectiveLock.Release(); }
    }

    private async Task RestoreCommunicationPerspectiveAsync(CancellationToken cancellationToken = default)
    {
        await _perspectiveLock.WaitAsync(cancellationToken);
        try { await ApplyCommunicationPerspectiveAsync(_connection, cancellationToken); }
        finally { _perspectiveLock.Release(); }
    }

    private async Task ApplyCommunicationPerspectiveAsync(
        HubConnection? connection,
        CancellationToken cancellationToken)
    {
        if (connection?.State != HubConnectionState.Connected) return;
        var organizationId = _perspectiveOrganizationId ?? Guid.Empty;
        try
        {
            await connection.InvokeAsync(
                "SetCommunicationPerspective",
                organizationId,
                _perspectiveAgentOrganizationUserId,
                cancellationToken);
        }
        catch (InvalidOperationException) when (connection.State != HubConnectionState.Connected) { }
    }

    private void Receive(AppRealtimeEventEnvelope envelope)
    {
        if (!_seen.Add(envelope.EventId)) return;
        _seenOrder.Enqueue(envelope.EventId);
        while (_seenOrder.Count > 512) _seen.Remove(_seenOrder.Dequeue());
        EventReceived?.Invoke(envelope);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _perspectiveLock.Dispose();
        _startLock.Dispose();
    }

    private sealed class BrowserCredentialsHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
