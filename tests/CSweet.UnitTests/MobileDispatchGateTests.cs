using System.Security.Claims;
using System.Text;
using CSweet.Api.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace CSweet.UnitTests;

/// <summary>Phone writes (TS-05 first cut): route marker, operation key bound to the body, and the antiforgery exemption.</summary>
public sealed class MobileDispatchGateTests
{
    [Theory]
    [InlineData(true, "k1", "{\"idempotencyKey\":\"k1\",\"x\":1}", 200)]
    [InlineData(true, null, "{\"idempotencyKey\":\"k1\"}", 428)]
    [InlineData(true, "k2", "{\"idempotencyKey\":\"k1\"}", 428)]
    [InlineData(true, "k1", "{\"x\":1}", 428)]
    [InlineData(false, "k1", "{\"idempotencyKey\":\"k1\"}", 404)]
    public async Task Phone_writes_need_a_marked_route_and_an_operation_key_matching_the_body(bool marked, string? key, string body, int expected)
    {
        var context = Mobile("POST", body, key, marked);
        string? seen = null;
        await new MobileDispatchGateMiddleware(async http => { seen = await new StreamReader(http.Request.Body).ReadToEndAsync(); http.Response.StatusCode = 200; })
            .InvokeAsync(context);
        Assert.Equal(expected, context.Response.StatusCode);
        if (expected == 200) Assert.Equal(body, seen); // the handler still reads the whole body
    }

    [Fact]
    public async Task Ordinary_requests_and_phone_reads_pass_the_gate_untouched()
    {
        var cookie = new DefaultHttpContext();
        cookie.Request.Method = "POST";
        var read = Mobile("GET", null, null, marked: false);
        foreach (var context in new[] { cookie, read })
        {
            var ran = false;
            await new MobileDispatchGateMiddleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(context);
            Assert.True(ran);
        }
    }

    [Theory]
    [InlineData("mobile", false)]   // dispatcher request: no token needed
    [InlineData("cookie", true)]    // browser session: token still required
    [InlineData("spoof", true)]     // device-looking principal without the dispatcher feature
    public async Task Only_dispatcher_requests_skip_antiforgery(string mode, bool validated)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.Path = "/api/organizations/x/documents/y/decisions";
        var type = mode == "cookie" ? "Identity.Application" : MobileRequestDispatcher.AuthenticationType;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], type));
        if (mode == "mobile") context.Features.Set(new MobileDispatchFeature(Guid.NewGuid()));
        var antiforgery = new RecordingAntiforgery();
        await new ApiAntiforgeryMiddleware(_ => Task.CompletedTask).InvokeAsync(context, antiforgery, new Environment());
        Assert.Equal(validated, antiforgery.Validated);
    }

    private static DefaultHttpContext Mobile(string method, string? body, string? key, bool marked)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.Features.Set(new MobileDispatchFeature(Guid.NewGuid()));
        context.Request.Method = method;
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }
        if (key is not null) context.Request.Headers[MobileDispatchGateMiddleware.OperationKeyHeader] = key;
        var metadata = marked ? new EndpointMetadataCollection(new AllowFromMobileAttribute(MobileAccess.Write, "test")) : EndpointMetadataCollection.Empty;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return context;
    }

    private sealed class RecordingAntiforgery : IAntiforgery
    {
        public bool Validated { get; private set; }
        public Task ValidateRequestAsync(HttpContext httpContext) { Validated = true; return Task.CompletedTask; }
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
    }

    private sealed class Environment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "CSweet.Api";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
