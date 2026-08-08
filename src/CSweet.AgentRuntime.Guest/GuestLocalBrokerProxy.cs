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
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };
    private Socket? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null) throw new InvalidOperationException("The guest broker proxy has already started.");
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The restricted guest broker requires a Linux Unix-domain socket.");
        var directory = Path.GetDirectoryName(socketPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(socketPath)) File.Delete(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        await GuestUnixFilePermissions.GrantWorkloadGroupAsync(
            socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            cancellationToken);
        _listener.Listen(16);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
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

    internal static async Task<GuestLocalBrokerRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
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
        if (requestLine.Length != 3 || requestLine[0] != "POST" ||
            requestLine[1] is not ("/mcp" or "/build/fetch" or "/build/artifact" or "/build/progress") ||
            !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new InvalidDataException("The local broker endpoint is not supported.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var separator = line.IndexOf(':');
            if (separator < 1) throw new InvalidDataException("A local broker header is malformed.");
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (!headers.TryAdd(key, value)) throw new InvalidDataException("Duplicate local broker headers are not accepted.");
        }
        var hasLength = headers.TryGetValue("Content-Length", out var lengthValue);
        var chunked = headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            string.Equals(transferEncoding, "chunked", StringComparison.OrdinalIgnoreCase);
        if (hasLength == chunked)
            throw new InvalidDataException("The local broker request must use one bounded HTTP body framing mode.");
        byte[] body;
        if (hasLength)
        {
            if (!int.TryParse(lengthValue, out var length) || length is < 0 or > MaximumBodyBytes)
                throw new InvalidDataException("The local broker content length is invalid.");
            body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
        }
        else
        {
            body = await ReadChunkedBodyAsync(stream, cancellationToken);
        }
        headers.Remove("Host");
        headers.Remove("Connection");
        headers.Remove("Content-Length");
        headers.Remove("Transfer-Encoding");
        return new GuestLocalBrokerRequest("POST", requestLine[1], headers, body);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadAsciiLineAsync(stream, 128, cancellationToken);
            var extension = sizeLine.IndexOf(';');
            var sizeValue = extension < 0 ? sizeLine : sizeLine[..extension];
            if (!int.TryParse(sizeValue, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException("The local broker chunk size is invalid.");
            if (size == 0)
            {
                var trailerBytes = 0;
                while (true)
                {
                    var trailer = await ReadAsciiLineAsync(stream, MaximumHeaderBytes, cancellationToken);
                    trailerBytes = checked(trailerBytes + trailer.Length + 2);
                    if (trailerBytes > MaximumHeaderBytes)
                        throw new InvalidDataException("The local broker chunk trailers exceed their limit.");
                    if (trailer.Length == 0) return body.ToArray();
                }
            }
            if (body.Length + size > MaximumBodyBytes)
                throw new InvalidDataException("The local broker chunked body exceeds its limit.");
            var chunk = new byte[size];
            await stream.ReadExactlyAsync(chunk, cancellationToken);
            await body.WriteAsync(chunk, cancellationToken);
            if (await ReadByteAsync(stream, cancellationToken) != '\r' ||
                await ReadByteAsync(stream, cancellationToken) != '\n')
                throw new InvalidDataException("The local broker chunk terminator is invalid.");
        }
    }

    private static async Task<string> ReadAsciiLineAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var previous = -1;
        while (line.Length <= maximumBytes)
        {
            var value = await ReadByteAsync(stream, cancellationToken);
            if (value < 0) throw new EndOfStreamException("The local broker chunked body ended unexpectedly.");
            if (previous == '\r' && value == '\n')
            {
                var bytes = line.ToArray();
                return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
            }
            line.WriteByte((byte)value);
            previous = value;
        }
        throw new InvalidDataException("The local broker chunk line exceeds its limit.");
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        return await stream.ReadAsync(value, cancellationToken) == 0 ? -1 : value[0];
    }

    internal static async Task WriteResponseAsync(Stream stream, GuestLocalBrokerResponse response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is < 100 or > 599 || response.Body.Length > MaximumBodyBytes)
            throw new InvalidDataException("The local broker response is invalid.");
        var head = new StringBuilder($"HTTP/1.1 {response.StatusCode} {Reason(response.StatusCode)}\r\n");
        foreach (var item in response.Headers)
        {
            if (item.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                HopByHopHeaders.Contains(item.Key)) continue;
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
