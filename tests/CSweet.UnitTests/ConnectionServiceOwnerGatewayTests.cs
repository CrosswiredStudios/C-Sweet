using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;
public sealed class ConnectionServiceOwnerGatewayTests
{
    [Fact]
    public async Task OwnerPreparationIsSeparateProtectedAndPinnedToOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "csweet-owner-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var selection = new ConnectionServiceSelectionStore(db, Options.Create(new PluginConnectionOptions { Managed = new() { Enabled = true, ServiceOrigin = "https://owner.example.test" } }));
            var handler = new Handler(); var path = Path.Combine(directory, "owner.json");
            var gateway = new ConnectionServiceOwnerGateway(new Factory(handler), new EphemeralDataProtectionProvider(), Options.Create(new ConnectionServiceOwnerOptions { RegistrationPath = path }), selection);
            var actor = Guid.NewGuid();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.Prepare(actor, cancelled.Token));
                Assert.False(File.Exists(path)); Assert.Empty(Directory.GetFiles(directory));
            }
            var parallel = await Task.WhenAll(gateway.Prepare(actor, default), gateway.Prepare(actor, default));
            var bootstrap = parallel[0]; Assert.Equal(bootstrap.BindingId, parallel[1].BindingId); Assert.Equal(bootstrap.PublicKey, parallel[1].PublicKey);
            Assert.Equal(actor, bootstrap.ActorId); Assert.Equal("bootstrap", bootstrap.Action);
            Assert.DoesNotContain(bootstrap.PublicKey, await File.ReadAllTextAsync(path));
            Assert.Equal(bootstrap, await gateway.Prepare(actor, default));
            handler.Bootstrap = bootstrap;
            var access = await gateway.Access(actor, default);
            Assert.Equal(actor, access.ActorId);
            await selection.Save(actor, new("SelfHosted", "https://different.example.test", 0), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.Access(actor, default));
            Assert.Equal(1, handler.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler : HttpMessageHandler
    {
        public CSweet.Contracts.Plugins.OwnerBootstrapDetails Bootstrap { get; set; } = null!;
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("https://owner.example.test/owner-control/v1/access", request.RequestUri!.AbsoluteUri);
            using var key = DeploymentProofCodec.PublicKey(Bootstrap.PublicKey);
            var proof = DeploymentProofCodec.Verify(request.Headers.GetValues(OwnerProtocol.ProofHeader).Single(), key);
            Assert.Equal(Bootstrap.BindingId, proof.DeploymentId);
            Assert.Equal("https://owner.example.test" + OwnerProtocol.AudienceSuffix, proof.Audience);
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(DeploymentProofCodec.Hash(body), proof.BodySha256);
            var envelope = JsonSerializer.Deserialize<OwnerRequest<JsonElement>>(body, DeploymentProofCodec.Json)!;
            Assert.Equal(Bootstrap.ActorId, envelope.ActorId);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OwnerAccess(new(Bootstrap.BindingId, Bootstrap.ServiceOrigin, Bootstrap.KeyId, 1), Bootstrap.ActorId, 1, "*", OwnerProtocol.Capabilities)) };
        }
    }
}
