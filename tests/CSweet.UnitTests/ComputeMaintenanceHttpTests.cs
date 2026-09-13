using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CSweet.Api.Compute;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fixture = CSweet.UnitTests.ComputeMaintenanceIngestorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceHttpTests
{
    internal sealed class Server(Fixture fixture, Action<IServiceCollection>? configureServices = null) : IAsyncDisposable
    {
        private ECDsa? tlsKey;
        private CngKey? windowsTlsKey;
        private string? windowsTlsKeyName;
        private X509Certificate2? certificate;
        private WebApplication? app;
        public Uri Https { get; private set; } = null!;
        public Uri Http { get; private set; } = null!;
        public string Mode { get; set; } = "normal";
        public int Captures { get; private set; }
        public bool SawCookie { get; private set; }
        public async Task StartAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                // Schannel requires an OS-backed key. This unique test key is explicitly deleted during teardown.
                windowsTlsKeyName = "csweet-compute-tls-test-" + Guid.NewGuid().ToString("N");
                windowsTlsKey = CngKey.Create(CngAlgorithm.ECDsaP256, windowsTlsKeyName, new CngKeyCreationParameters
                { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, KeyUsage = CngKeyUsages.Signing });
                tlsKey = new ECDsaCng(windowsTlsKey);
            }
            else tlsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", tlsKey, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders(); builder.Logging.AddConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            });
            builder.Services.AddSingleton(fixture.Receiver);
            builder.Services.AddComputeProviderIngress();
            configureServices?.Invoke(builder.Services);
            app = builder.Build(); app.UseRouting(); app.UseRateLimiter();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api/compute/providers"))
                {
                    SawCookie |= !string.IsNullOrEmpty(context.Request.Headers.Cookie);
                    context.Response.Headers.Append("Set-Cookie", "unwanted=session; Path=/; Secure; HttpOnly");
                    if (Mode == "redirect") { context.Response.Redirect("/capture", permanent: false, preserveMethod: true); return; }
                    if (Mode == "wrong-ack")
                    {
                        await context.Response.WriteAsJsonAsync(new ComputeMaintenanceAcknowledgement(Guid.NewGuid(), "wrong")); return;
                    }
                    if (Mode == "oversized-ack")
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(new string('x', ComputeMaintenanceTransport.MaximumAcknowledgementBytes + 1)); return;
                    }
                }
                await next(context);
            });
            app.MapComputeProviderEndpoints();
            app.MapPost("/capture", () => { Captures++; return Results.Ok(); });
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            Https = new(addresses.Single(x => x.StartsWith("https://", StringComparison.Ordinal)));
            Http = new(addresses.Single(x => x.StartsWith("http://", StringComparison.Ordinal)));
        }
        internal HttpClientHandler Handler()
        {
            var handler = ComputeMaintenanceHttpClient.CreateHandler();
            // Pin only this temporary server certificate; production retains default TLS validation.
            handler.ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null && certificate is not null && presented.RawData.AsSpan().SequenceEqual(certificate.RawData);
            return handler;
        }
        public ComputeMaintenanceHttpClient DeliveryClient() => new(Https, fixture.Signer, Handler());
        public HttpClient RawClient() => new(Handler()) { Timeout = TimeSpan.FromSeconds(10) };
        public async ValueTask DisposeAsync()
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            certificate?.Dispose(); tlsKey?.Dispose();
            if (OperatingSystem.IsWindows() && windowsTlsKey is not null)
            {
                windowsTlsKey.Delete(); windowsTlsKey.Dispose();
                Assert.False(CngKey.Exists(windowsTlsKeyName!, CngProvider.MicrosoftSoftwareKeyStorageProvider));
            }
        }
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Real_https_delivery_acknowledges_only_committed_ledger_events_without_cookies()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await using var server = new Server(f); await server.StartAsync();
        using var client = server.DeliveryClient();
        Assert.Equal(2, await new ComputeMaintenanceOutboxDispatcher(f.Provider.Journal.Journal()).DispatchAsync(client.DeliverAsync, default));
        Assert.Equal(2, await f.Db.AuditEvents.CountAsync());
        Assert.Empty(await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        Assert.False(server.SawCookie);
        Assert.True((await f.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("wrong-ack")]
    [InlineData("oversized-ack")]
    public async Task Redirects_or_unexpected_acknowledgements_leave_provider_evidence_pending(string mode)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await using var server = new Server(f) { Mode = mode }; await server.StartAsync();
        using var client = server.DeliveryClient();
        await Assert.ThrowsAsync<IOException>(() => new ComputeMaintenanceOutboxDispatcher(f.Provider.Journal.Journal()).DispatchAsync(client.DeliverAsync, default));
        Assert.Equal(0, server.Captures);
        Assert.Equal(2, (await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(100, default)).Count);
        Assert.Equal(0, await f.Db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Endpoint_rejects_plain_http_invalid_json_encoding_and_declared_or_chunked_oversize_bodies()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await using var server = new Server(f); await server.StartAsync(); using var client = server.RawClient();
        Assert.Throws<ArgumentException>(() => new ComputeMaintenanceHttpClient(server.Http, f.Signer));
        using (var plain = await client.PostAsync(new Uri(server.Http, ComputeMaintenanceTransport.Path), new StringContent("{}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
        var url = new Uri(server.Https, ComputeMaintenanceTransport.Path);
        using (var invalid = await client.PostAsync(url, new StringContent("{", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var encoded = new StringContent("{}", Encoding.UTF8, "application/json"); encoded.Headers.ContentEncoding.Add("gzip");
        using (var response = await client.PostAsync(url, encoded)) Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var oversized = new byte[ComputeMaintenanceTransport.MaximumRequestBytes + 1];
        using var declared = new ByteArrayContent(oversized); declared.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        // Let the server reject oversized declared length before the client streams a body into a closed connection.
        using var declaredRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = declared };
        declaredRequest.Headers.ExpectContinue = true;
        using (var response = await client.SendAsync(declaredRequest)) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var chunked = new ChunkedContent(oversized); chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var chunkClient = server.RawClient();
        using (var response = await chunkClient.PostAsync(url, chunked)) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await f.Db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Ingress_limits_unauthenticated_request_rate_before_ledger_processing()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await using var server = new Server(f); await server.StartAsync(); using var client = server.RawClient();
        var url = new Uri(server.Http, ComputeMaintenanceTransport.Path);
        for (var i = 0; i < 121; i++)
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content);
            Assert.Equal(i < 120 ? HttpStatusCode.Forbidden : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        Assert.Equal(0, await f.Db.AuditEvents.CountAsync());
    }
}
