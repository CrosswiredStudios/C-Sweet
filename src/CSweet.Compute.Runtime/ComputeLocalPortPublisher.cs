using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Explicit, leased forwarding to a guest loopback port. Never connects to an agent-supplied host address.</summary>
public sealed class ComputeLocalPortPublisher : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Publication> publications = new();

    public async Task<ComputeWorkloadResult> PublishAsync(Guid environmentId, int guestPort, DateTimeOffset expiresAt,
        Func<CancellationToken, Task<Stream>> connectOwnedGuest, CancellationToken token)
    {
        if (guestPort is < 1024 or > 65535 || expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddHours(24))
            throw new ArgumentException("Invalid publication lease.");
        // Verify that the app actually answers before returning a test URL.
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            probe.CancelAfter(TimeSpan.FromSeconds(5));
            await using var guest = await ConnectAsync(connectOwnedGuest, guestPort, probe.Token);
            await guest.WriteAsync("GET / HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray(), probe.Token);
            await guest.FlushAsync(probe.Token);
            var status = new byte[12];
            await guest.ReadExactlyAsync(status, probe.Token);
            var line = System.Text.Encoding.ASCII.GetString(status);
            if (line is null || !(line.StartsWith("HTTP/1.0 2", StringComparison.Ordinal) || line.StartsWith("HTTP/1.1 2", StringComparison.Ordinal)))
                throw new IOException("Guest application did not pass its HTTP health check.");
        }
        Remove(environmentId);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        var lifetime = new CancellationTokenSource(expiresAt - DateTimeOffset.UtcNow);
        var publication = new Publication(listener, lifetime);
        if (!publications.TryAdd(environmentId, publication)) { publication.Dispose(); throw new IOException("Publication conflict."); }
        publication.Running = RunAsync(publication, connectOwnedGuest, guestPort);
        return new(Url: $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/", UrlExpiresAt: expiresAt);
    }

    public void Remove(Guid environmentId)
    {
        if (publications.TryRemove(environmentId, out var publication)) publication.Dispose();
    }
    public void Dispose() { foreach (var id in publications.Keys) Remove(id); }

    private static async Task RunAsync(Publication publication, Func<CancellationToken, Task<Stream>> connect, int port)
    {
        var clients = new List<Task>();
        var token = publication.Lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                clients.RemoveAll(t => t.IsCompleted);
                var client = await publication.Listener.AcceptTcpClientAsync(token);
                if (clients.Count >= 8) { client.Dispose(); continue; }
                clients.Add(ForwardAsync(client, connect, port, token));
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { publication.Listener.Stop(); await Task.WhenAll(clients); }
    }

    private static async Task ForwardAsync(TcpClient client, Func<CancellationToken, Task<Stream>> connect, int port, CancellationToken token)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await using var guest = await ConnectAsync(connect, port, deadline.Token);
                await ComputeStreamRelay.RunAsync(client.GetStream(), guest, deadline.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException or System.Text.Json.JsonException) { }
        }
    }

    private static async Task<Stream> ConnectAsync(Func<CancellationToken, Task<Stream>> connect, int port, CancellationToken token)
    {
        var stream = await connect(token);
        try
        {
            var challenge = Guid.NewGuid();
            await ComputeGuestWire.WriteAsync(stream, new ComputeGuestWireRequest(1, challenge, ForwardPort: port), ComputeGuestWire.MaximumRequestBytes, token);
            var acknowledgement = await ComputeGuestWire.ReadAsync<ComputeGuestForwardReady>(stream, 1024, token);
            if (acknowledgement.Version != 1 || acknowledgement.Challenge != challenge || !acknowledgement.Connected)
                throw new IOException("Guest port is unavailable.");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private sealed class Publication(TcpListener listener, CancellationTokenSource lifetime) : IDisposable
    {
        public TcpListener Listener { get; } = listener;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public Task? Running { get; set; }
        public void Dispose() { Lifetime.Cancel(); Listener.Stop(); /* running relays observe cancellation */ }
    }
}
