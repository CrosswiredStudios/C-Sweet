using System.Net;
using System.Net.Http.Json;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class TrustedServiceAuthenticationTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task SignedInternalRequestIsAcceptedOnce()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero));
        var signed = await SignAsync(time, new { expectedHeadSha = new string('a', 40) });
        var options = AuthenticationOptions();
        var replay = new TrustedRequestReplayCache(time);
        var called = false;
        var middleware = new TrustedServiceAuthenticationMiddleware(
            _ => { called = true; return Task.CompletedTask; }, options, replay, time);

        var first = await CreateContextAsync(signed);
        await middleware.InvokeAsync(first);
        Assert.True(called);

        called = false;
        var replayed = await CreateContextAsync(signed);
        await middleware.InvokeAsync(replayed);
        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, replayed.Response.StatusCode);
    }

    [Fact]
    public async Task BodyTamperingIsRejected()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero));
        var signed = await SignAsync(time, new { expectedHeadSha = new string('a', 40) });
        var context = await CreateContextAsync(signed);
        context.Request.Body = new MemoryStream("{\"expectedHeadSha\":\"tampered\"}"u8.ToArray());
        context.Request.ContentLength = context.Request.Body.Length;
        var called = false;
        var middleware = new TrustedServiceAuthenticationMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            AuthenticationOptions(), new TrustedRequestReplayCache(time), time);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingSignatureIsRejectedBeforeEndpoint()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/internal/v2/pull-requests/merge-exact";
        var called = false;
        var middleware = new TrustedServiceAuthenticationMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            AuthenticationOptions(), new TrustedRequestReplayCache(time), time);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidServerKeyIsRejectedBeforeEndpoint()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var signed = await SignAsync(time, new { value = "test" });
        var context = await CreateContextAsync(signed);
        var called = false;
        var middleware = new TrustedServiceAuthenticationMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Options.Create(new TrustedServiceAuthenticationOptions
            {
                KeyId = "core",
                SharedKeyBase64 = string.Empty
            }),
            new TrustedRequestReplayCache(time),
            time);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static IOptions<TrustedServiceAuthenticationOptions> AuthenticationOptions() =>
        Options.Create(new TrustedServiceAuthenticationOptions
        {
            KeyId = "core",
            SharedKeyBase64 = Convert.ToBase64String(Key),
            AllowedClockSkewSeconds = 120
        });

    private static async Task<CapturedRequest> SignAsync(TimeProvider time, object payload)
    {
        var capture = new CaptureHandler();
        var signer = new TrustedServiceAuthenticationHandler(AuthenticationOptions(), time)
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(signer) { BaseAddress = new Uri("http://githost/") };
        using var response = await client.PostAsJsonAsync(
            "internal/v2/pull-requests/merge-exact", payload);
        return capture.Request ?? throw new InvalidOperationException("The request was not captured.");
    }

    private static async Task<DefaultHttpContext> CreateContextAsync(CapturedRequest request)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = request.Method;
        context.Request.Path = request.Path;
        context.Request.QueryString = new QueryString(request.Query);
        context.Request.Body = new MemoryStream(request.Body);
        context.Request.ContentLength = request.Body.Length;
        foreach (var header in request.Headers)
            context.Request.Headers[header.Key] = header.Value;
        context.Response.Body = new MemoryStream();
        await Task.CompletedTask;
        return context;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken),
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        string Query,
        byte[] Body,
        IReadOnlyDictionary<string, string[]> Headers);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
