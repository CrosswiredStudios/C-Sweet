using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CSweet.AgentRuntime.Firecracker.Helper;

internal sealed class FirecrackerApiClient : IDisposable
{
    private const int MaximumErrorBytes = 16 * 1024;
    private readonly HttpClient _client;

    public FirecrackerApiClient(string socketPath)
    {
        if (!Path.IsPathFullyQualified(socketPath))
            throw new ArgumentException("The Firecracker API socket path must be absolute.", nameof(socketPath));
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task PutAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string> GetInstanceStateAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadBoundedAsync(stream, 64 * 1024, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            throw new FirecrackerApiException("Firecracker omitted its instance state.");
        return state.GetString() ?? string.Empty;
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/version");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        while (bounded.Length < MaximumErrorBytes)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, MaximumErrorBytes - (int)bounded.Length)),
                cancellationToken);
            if (read == 0) break;
            bounded.Write(buffer, 0, read);
        }
        var detail = Encoding.UTF8.GetString(bounded.ToArray());
        detail = new string(detail.Where(character => !char.IsControl(character)).Take(512).ToArray());
        throw new FirecrackerApiException(
            $"Firecracker API returned {(int)response.StatusCode}: {(string.IsNullOrWhiteSpace(detail) ? "unspecified" : detail)}");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var remaining = maximumBytes + 1 - (int)output.Length;
            if (remaining <= 0) throw new FirecrackerApiException("The Firecracker API response exceeded its limit.");
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
        }
    }
}

internal sealed class FirecrackerApiException(string message) : Exception(message);
