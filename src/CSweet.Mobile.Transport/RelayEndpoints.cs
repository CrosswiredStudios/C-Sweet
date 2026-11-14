using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CSweet.Mobile.Transport;

/// <summary>An authenticated phone session at Headquarters. <see cref="PhonePin"/> is the TLS client key pin, equal to the key the relay admitted.</summary>
public sealed record PhoneChannel(string ChannelId, string PhonePin, SslProtocols Protocol);

/// <summary>
/// Headquarters' outbound connection to the relay. Attaches its route with the route key, then answers each phone
/// channel as the TLS server, requiring the phone's enrolled client key. Re-attaches after a relay reconnect.
/// </summary>
public sealed class HeadquartersRelayConnector : IRelayHandler, IAsyncDisposable
{
    private readonly IRelayConnection _relay;
    private readonly string _routeId;
    private readonly ECDsa _routeKey;
    private readonly X509Certificate2 _certificate;
    private readonly Func<string, bool> _isEnrolledPhone;
    private readonly Func<SslStream, PhoneChannel, CancellationToken, Task> _serve;
    private readonly ConcurrentDictionary<string, RelayChannelStream> _channels = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HeadquartersRelayConnector(IRelayConnection relay, string routeId, ECDsa routeKey, X509Certificate2 certificate,
        Func<string, bool> isEnrolledPhone, Func<SslStream, PhoneChannel, CancellationToken, Task> serve)
    {
        _relay = relay;
        _routeId = routeId;
        _routeKey = routeKey;
        _certificate = certificate;
        _isEnrolledPhone = isEnrolledPhone;
        _serve = serve;
        relay.SetHandler(this);
    }

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    public string? DetachedReason { get; private set; }

    /// <summary>Completes when this connector can no longer serve: replaced by another connector or the relay connection closed.</summary>
    public Task<string> Ended => _ended.Task;
    public int OpenChannels => _channels.Count;

    public async Task StartAsync(CancellationToken ct)
    {
        await _relay.StartAsync(ct);
        await AttachAsync(ct);
    }

    private async Task AttachAsync(CancellationToken ct)
    {
        var nonce = await _relay.ChallengeAsync(ct);
        await _relay.AttachHeadquartersAsync(_routeId, RelayProtocol.Sign(_routeKey, RelayProtocol.AttachMessage(_routeId, nonce)), ct);
        DetachedReason = null;
    }

    Task IRelayHandler.OnChannelOpenedAsync(string channelId, string phoneKeyThumbprint)
    {
        var stream = new RelayChannelStream(_relay, channelId);
        if (!_channels.TryAdd(channelId, stream)) return Task.CompletedTask;
        _ = Task.Run(() => ServeAsync(stream, phoneKeyThumbprint));
        return Task.CompletedTask;
    }

    private async Task ServeAsync(RelayChannelStream stream, string admittedThumbprint)
    {
        try
        {
            // The TLS client key must be the key the relay admitted with the ticket, and an enrolled phone.
            await using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                await tls.AuthenticateAsServerAsync(PinnedTls.ServerOptions(_certificate,
                    pin => pin == admittedThumbprint && _isEnrolledPhone(pin)), handshake.Token);
            }
            await _serve(tls, new PhoneChannel(stream.ChannelId, admittedThumbprint, tls.SslProtocol), _stopping.Token);
        }
        catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            // Handshake refused, tampered or closed channel: the channel ends. Nothing from it is reused.
        }
        finally
        {
            _channels.TryRemove(stream.ChannelId, out _);
            await stream.DisposeAsync();
        }
    }

    Task IRelayHandler.OnFrameAsync(string channelId, byte[] data) =>
        _channels.TryGetValue(channelId, out var stream) ? stream.DeliverAsync(data) : Task.CompletedTask;

    Task IRelayHandler.OnChannelClosedAsync(string channelId, string reason)
    {
        if (_channels.TryGetValue(channelId, out var stream)) stream.Complete(reason);
        return Task.CompletedTask;
    }

    Task IRelayHandler.OnDetachedAsync(string reason)
    {
        DetachedReason = reason;
        foreach (var stream in _channels.Values) stream.Complete("detached");
        _ended.TrySetResult("detached:" + reason);
        return Task.CompletedTask;
    }

    Task IRelayHandler.OnClosedAsync(Exception? error)
    {
        foreach (var stream in _channels.Values) stream.Complete("relay_closed");
        _ended.TrySetResult("closed");
        return Task.CompletedTask;
    }

    Task IRelayHandler.OnReconnectedAsync()
    {
        foreach (var stream in _channels.Values) stream.Complete("relay_reconnected");
        return AttachAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var stream in _channels.Values) stream.Complete("stopping");
        await _relay.DisposeAsync();
        _stopping.Dispose();
    }
}

/// <summary>An open, authenticated phone-to-Headquarters session. Dispose to close the channel.</summary>
public sealed class PhoneRelaySession : IRelayHandler, IAsyncDisposable
{
    private readonly IRelayConnection _relay;
    private readonly bool _ownsRelay;
    private RelayChannelStream? _stream;

    private PhoneRelaySession(IRelayConnection relay, bool ownsRelay)
    {
        _relay = relay;
        _ownsRelay = ownsRelay;
        relay.SetHandler(this);
    }

    public SslStream Stream { get; private set; } = default!;
    public SslProtocols Protocol => Stream.SslProtocol;
    public string? CloseReason => _stream?.CloseReason;

    /// <summary>
    /// Opens a channel with the admission ticket, proves the phone key to the relay, and completes TLS with
    /// Headquarters pinned to <paramref name="headquartersPin"/>, presenting the phone certificate.
    /// </summary>
    public static async Task<PhoneRelaySession> ConnectAsync(IRelayConnection relay, string routeId, string ticket, ECDsa phoneKey,
        X509Certificate2 phoneCertificate, string headquartersPin, CancellationToken ct, bool startRelay = true, bool ownsRelay = true)
    {
        var session = new PhoneRelaySession(relay, ownsRelay);
        try
        {
            if (startRelay) await relay.StartAsync(ct);
            var nonce = await relay.ChallengeAsync(ct);
            var channelId = await relay.OpenChannelAsync(routeId, ticket, RelayProtocol.PublicKey(phoneKey),
                RelayProtocol.Sign(phoneKey, RelayProtocol.OpenMessage(routeId, nonce)), ct);
            session._stream = new RelayChannelStream(relay, channelId);
            session.Stream = new SslStream(session._stream, leaveInnerStreamOpen: false);
            await session.Stream.AuthenticateAsClientAsync(PinnedTls.ClientOptions(headquartersPin, phoneCertificate), ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    Task IRelayHandler.OnChannelOpenedAsync(string channelId, string phoneKeyThumbprint) => Task.CompletedTask;

    Task IRelayHandler.OnFrameAsync(string channelId, byte[] data) =>
        _stream is { } stream && stream.ChannelId == channelId ? stream.DeliverAsync(data) : Task.CompletedTask;

    Task IRelayHandler.OnChannelClosedAsync(string channelId, string reason)
    {
        if (_stream is { } stream && stream.ChannelId == channelId) stream.Complete(reason);
        return Task.CompletedTask;
    }

    Task IRelayHandler.OnDetachedAsync(string reason) => Task.CompletedTask;

    Task IRelayHandler.OnReconnectedAsync()
    {
        _stream?.Complete("relay_reconnected");
        return Task.CompletedTask;
    }

    Task IRelayHandler.OnClosedAsync(Exception? error)
    {
        _stream?.Complete("relay_closed");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Stream is not null) await Stream.DisposeAsync();
        else if (_stream is not null) await _stream.DisposeAsync();
        if (_ownsRelay) await _relay.DisposeAsync();
    }
}
