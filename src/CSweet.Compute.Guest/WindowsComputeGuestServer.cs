using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Guest;

public static class WindowsComputeGuestServer
{
    public static async Task RunAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException();
        var execution = new ComputeGuestExecution("windows", command =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException();
            return new WindowsComputeGuestProcess(command, Path.GetTempPath());
        });
        var handler = new ComputeGuestConnection(execution);
        using var listener = new Socket((AddressFamily)34, SocketType.Stream, (ProtocolType)1);
        // HV_GUID_PARENT binding admits only the parent host and is unsupported on bare-metal hosts.
        // No wildcard, loopback, TCP, or same-partition command fallback.
        listener.Bind(new ParentEndpoint()); listener.Listen(8);
        using var cancellation = token.Register(listener.Dispose);
        var connections = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var failed in connections.Where(task => task.IsFaulted))
                    _ = failed.Exception;
                connections.RemoveAll(task => task.IsCompleted);
                if (connections.Count >= 8) { await Task.WhenAny(connections).WaitAsync(token); continue; }
                // AF_HYPERV does not support the IP-specific AcceptEx path on every Windows build.
                var socket = await Task.Run(listener.Accept, CancellationToken.None);
                connections.Add(ServeAsync(socket));
            }
        }
        catch (Exception error) when (token.IsCancellationRequested && error is SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { await Task.WhenAll(connections); }

        async Task ServeAsync(Socket socket)
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            try { await handler.ServeAsync(stream, token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or IOException or JsonException or ArgumentException or InvalidOperationException)
            { Console.Error.WriteLine("compute-guest-request-failed"); }
        }
    }

    private sealed class ParentEndpoint : EndPoint
    {
        private static readonly Guid Parent = new("a42e7cda-d03f-480c-9cc2-a4de20abb878");
        private static readonly Guid Service = new($"{ComputeGuestWire.Port:x8}-facb-11e6-bd58-64006a7986d3");
        public override AddressFamily AddressFamily => (AddressFamily)34;
        public override SocketAddress Serialize()
        {
            var address = new SocketAddress(AddressFamily, 36);
            var parent = Parent.ToByteArray(); var service = Service.ToByteArray();
            for (var index = 0; index < 16; index++) { address[4 + index] = parent[index]; address[20 + index] = service[index]; }
            return address;
        }
        public override EndPoint Create(SocketAddress socketAddress) => this;
    }
}
