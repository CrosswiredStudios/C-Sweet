using System.IO.Pipes;
using System.Net.Sockets;

namespace CSweet.AgentRuntime.LocalRpc;

public static class LocalRuntimeHostTransport
{
    public static async Task<Stream> ConnectAsync(
        RuntimeHostEndpointOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".",
                options.NamedPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.UnixSocketPath), timeout.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
