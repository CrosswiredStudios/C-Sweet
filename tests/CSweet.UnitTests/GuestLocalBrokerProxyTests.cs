using System.Text;
using CSweet.AgentRuntime.Guest;

namespace CSweet.UnitTests;

public sealed class GuestLocalBrokerProxyTests
{
    [Fact]
    public async Task ReadRequestAsync_AcceptsBoundedChunkedJsonContent()
    {
        const string body = "{\"jsonrpc\":\"2.0\"}";
        var first = body[..8];
        var second = body[8..];
        var wire =
            "POST /mcp HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            $"{first.Length:x}\r\n{first}\r\n" +
            $"{second.Length:x}\r\n{second}\r\n" +
            "0\r\n\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        var request = await GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal("POST", request.Method);
        Assert.Equal("/mcp", request.Path);
        Assert.Equal(body, Encoding.UTF8.GetString(request.Body.Span));
        Assert.DoesNotContain("Transfer-Encoding", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsConflictingBodyFraming()
    {
        const string wire =
            "POST /mcp HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 0\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteResponseAsync_ReframesBufferedResponseWithoutHopByHopHeaders()
    {
        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":{}}");
        var response = new GuestLocalBrokerResponse(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Transfer-Encoding"] = "chunked",
                ["Connection"] = "keep-alive",
                ["Keep-Alive"] = "timeout=5"
            },
            body);
        await using var stream = new MemoryStream();

        await GuestLocalBrokerProxy.WriteResponseAsync(stream, response, CancellationToken.None);

        var wire = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Contains($"Content-Length: {body.Length}\r\n", wire, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Transfer-Encoding", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep-Alive", wire, StringComparison.OrdinalIgnoreCase);
    }
}
