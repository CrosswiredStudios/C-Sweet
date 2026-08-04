using System.Net;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentBrokerAuthenticationTests
{
    private static readonly byte[] AgentKey = Enumerable.Repeat((byte)0x41, 32).ToArray();
    private static readonly byte[] GitHostKey = Enumerable.Repeat((byte)0x47, 32).ToArray();

    [Fact]
    public async Task AgentBrokerSignatureIsAcceptedByCoreBrokerOnly()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 3, 22, 0, 0, TimeSpan.Zero));
        var captured = await SignAgentRequestAsync(time, "agent-broker/v2/workspaces/prepare");
        var accepted = false;
        var agentMiddleware = new AgentBrokerAuthenticationMiddleware(
            _ => { accepted = true; return Task.CompletedTask; },
            AgentOptions(),
            new TrustedRequestReplayCache(time),
            time);
        var agentContext = CreateContext(captured, "/agent-broker/v2/workspaces/prepare");

        await agentMiddleware.InvokeAsync(agentContext);

        Assert.True(accepted);

        var trustedCalled = false;
        var trustedMiddleware = new TrustedServiceAuthenticationMiddleware(
            _ => { trustedCalled = true; return Task.CompletedTask; },
            Options.Create(new TrustedServiceAuthenticationOptions
            {
                KeyId = "core",
                SharedKeyBase64 = Convert.ToBase64String(GitHostKey)
            }),
            new TrustedRequestReplayCache(time),
            time);
        var internalCapture = await SignAgentRequestAsync(time, "internal/v2/workspaces/prepare");
        var trustedContext = CreateContext(internalCapture, "/internal/v2/workspaces/prepare");

        await trustedMiddleware.InvokeAsync(trustedContext);

        Assert.False(trustedCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, trustedContext.Response.StatusCode);
    }

    private static IOptions<AgentBrokerAuthenticationOptions> AgentOptions() =>
        Options.Create(new AgentBrokerAuthenticationOptions
        {
            KeyId = "agenthost",
            SharedKeyBase64 = Convert.ToBase64String(AgentKey)
        });

    private static async Task<Captured> SignAgentRequestAsync(TimeProvider time, string path)
    {
        var capture = new CaptureHandler();
        var signer = new AgentBrokerAuthenticationHandler(AgentOptions(), time) { InnerHandler = capture };
        using var client = new HttpClient(signer) { BaseAddress = new Uri("http://core/") };
        using var response = await client.PostAsync(
            path,
            new ByteArrayContent("{}"u8.ToArray()));
        return capture.Value!;
    }

    private static DefaultHttpContext CreateContext(Captured captured, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(captured.Body);
        foreach (var header in captured.Headers)
            context.Request.Headers[header.Key] = header.Value;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Captured? Value { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Value = new Captured(
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record Captured(byte[] Body, IReadOnlyDictionary<string, string[]> Headers);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
