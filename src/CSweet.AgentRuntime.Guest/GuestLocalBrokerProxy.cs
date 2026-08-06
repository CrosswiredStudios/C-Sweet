using System.Net.Sockets;
using System.Text;

namespace CSweet.AgentRuntime.Guest;

public sealed record GuestLocalBrokerRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body);

public sealed record GuestLocalBrokerResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body);

/// <summary>
/// Minimal loopback-free HTTP/1.1 endpoint for the agent SDK. It listens only on a
/// guest-local Unix socket and forwards complete bounded requests through the
/// authenticated host/guest broker channel.
/// </summary>
public sealed class GuestLocalBrokerProxy(
    string socketPath,
    Func<GuestLocalBrokerRequest, CancellationToken, Task<GuestLocalBrokerResponse>> forward) : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private const int MaximumBodyBytes = 1024 * 1024;
    private Socket? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null) throw new InvalidOperationException("The guest broker proxy has already started.");
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The restricted guest broker requires a Linux Unix-domain socket.");
        var directory = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(socketPath)) File.Delete(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(16);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            _ = HandleConnectionSafeAsync(client, cancellationToken);
        }
    }

    private async Task HandleConnectionSafeAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            try
            {
                var request = await ReadRequestAsync(stream, cancellationToken);
                var response = await forward(request, cancellationToken);
                await WriteResponseAsync(stream, response, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                await WriteResponseAsync(
                    stream,
                    new GuestLocalBrokerResponse(400, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("broker request rejected")),
                    CancellationToken.None);
            }
        }
    }

    private static async Task<GuestLocalBrokerRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var terminatorState = 0;
        while (header.Length < MaximumHeaderBytes)
        {
            var value = new byte[1];
            if (await stream.ReadAsync(value, cancellationToken) == 0)
                throw new EndOfStreamException("The local broker request ended before its headers.");
            header.WriteByte(value[0]);
            terminatorState = (terminatorState, value[0]) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0
            };
            if (terminatorState == 4) break;
        }
        if (terminatorState != 4) throw new InvalidDataException("The local broker headers exceed their limit.");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || requestLine[0] != "POST" || requestLine[1] != "/mcp" || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new InvalidDataException("Only POST /mcp is supported by the local broker.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var separator = line.IndexOf(':');
            if (separator < 1) throw new InvalidDataException("A local broker header is malformed.");
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (!headers.TryAdd(key, value)) throw new InvalidDataException("Duplicate local broker headers are not accepted.");
        }
        if (!headers.TryGetValue("Content-Length", out var lengthValue) ||
            !int.TryParse(lengthValue, out var length) || length is < 0 or > MaximumBodyBytes)
            throw new InvalidDataException("The local broker content length is invalid.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        headers.Remove("Host");
        headers.Remove("Connection");
        headers.Remove("Content-Length");
        headers.Remove("Transfer-Encoding");
        return new GuestLocalBrokerRequest("POST", "/mcp", headers, body);
    }

    private static async Task WriteResponseAsync(Stream stream, GuestLocalBrokerResponse response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is < 100 or > 599 || response.Body.Length > MaximumBodyBytes)
            throw new InvalidDataException("The local broker response is invalid.");
        var head = new StringBuilder($"HTTP/1.1 {response.StatusCode} {Reason(response.StatusCode)}\r\n");
        foreach (var item in response.Headers)
        {
            if (item.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                item.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            head.Append(item.Key).Append(": ").Append(item.Value).Append("\r\n");
        }
        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken);
        await stream.WriteAsync(response.Body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null) await _lifetime.CancelAsync();
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (SocketException) { }
        }
        _lifetime?.Dispose();
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        413 => "Content Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Broker Response"
    };
}
