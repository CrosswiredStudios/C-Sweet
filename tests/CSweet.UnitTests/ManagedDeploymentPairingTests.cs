using System.Net;
using System.Net.Http.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ManagedDeploymentPairingTests
{
    [Fact]
    public async Task PairingPersistsOnlyProtectedKeyAndVerifiesActualDeploymentProofBeforeActivation()
    {
        using var f = new Fixture();
        var pairing = await f.Gateway.PreparePairing(default);
        Assert.Equal(pairing, await f.Gateway.PreparePairing(default));
        var stored = await File.ReadAllTextAsync(f.Options.RegistrationPath);
        Assert.DoesNotContain("privateKey", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pairing.PublicKey, stored);
        Assert.False((await f.Gateway.Status(default)).Paired);
        f.Http.Pairing = pairing;
        await f.Gateway.CompletePairing(f.Http.Registration, default);
        Assert.True((await f.Gateway.Status(default)).Paired);
        Assert.Equal(1, f.Http.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gateway.PreparePairing(default));
        var restarted = f.CreateGateway();
        Assert.True((await restarted.Status(default)).Paired);
        var wrongKeyRing = new ManagedPluginConnectionGateway(f.Http, new EphemeralDataProtectionProvider(), Options.Create(new PluginConnectionOptions { Managed = f.Options }));
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => wrongKeyRing.Status(default));
    }

    [Fact]
    public async Task ServiceFailureOrDifferentTenantReceiptCannotPairOrTriggerLocalFallback()
    {
        using var f = new Fixture(); f.Http.Pairing = await f.Gateway.PreparePairing(default);
        f.Http.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Gateway.CompletePairing(f.Http.Registration, default));
        Assert.False((await f.Gateway.Status(default)).Paired);
        f.Http.Fail = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Gateway.CompletePairing(f.Http.Registration with { TenantId = "different-tenant" }, default));
        Assert.False((await f.Gateway.Status(default)).Paired);
        Assert.True(f.Gateway.UsesManaged("provider.one"));
        f.Options.CustomerOwnedProfiles = ["customer-owned"];
        Assert.False(f.Gateway.UsesManaged("customer-owned"));
        Assert.True(f.Gateway.UsesManaged("provider.one"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPairingCanCancelAfterModeChangeWhileNewManagedAccessRemainsDisabled(bool disableService)
    {
        using var f = new Fixture(); f.Http.Pairing = await f.Gateway.PreparePairing(default);
        await f.Gateway.CompletePairing(f.Http.Registration, default);
        f.Options.CustomerOwnedProfiles = ["provider.one"];
        f.Options.Enabled = !disableService;
        Assert.False(f.Gateway.UsesManaged("provider.one"));
        f.Http.CleanupAttempt = Guid.NewGuid();
        var restarted = f.CreateGateway();
        var cancelled = await restarted.CancelAttempt(f.Http.CleanupAttempt.Value, default);
        Assert.Equal("Cancelled", cancelled.Status);
        if (disableService)
        {
            var requests = f.Http.Requests;
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Availability(new string('a', 64), default));
            Assert.Equal(requests, f.Http.Requests);
        }
    }

    [Theory]
    [InlineData("http://service.example.com", "https://host.example/settings/plugins/managed-return")]
    [InlineData("https://service.example.com/arbitrary", "https://host.example/settings/plugins/managed-return")]
    [InlineData("https://service.example.com", "https://host.example/wrong-return")]
    [InlineData("https://service.example.com", "http://public-host.example/settings/plugins/managed-return")]
    public async Task PairingRejectsInexactOrInsecureServiceAndReturnAddresses(string service, string target)
    {
        using var f = new Fixture(); f.Options.ServiceOrigin = service; f.Options.ReturnUri = target;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Gateway.PreparePairing(default));
        Assert.False(File.Exists(f.Options.RegistrationPath)); Assert.Equal(0, f.Http.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "CSweet.ManagedPairing." + Guid.NewGuid().ToString("N"));
        private readonly EphemeralDataProtectionProvider protection = new();
        public readonly ProofService Http = new();
        public ManagedPluginConnectionOptions Options { get; }
        public ManagedPluginConnectionGateway Gateway { get; }
        public Fixture()
        {
            Options = new() { Enabled = true, ServiceOrigin = "https://service.example.com", ReturnUri = "https://host.example/settings/plugins/managed-return", RegistrationPath = Path.Combine(folder, "registration.dat") };
            Gateway = CreateGateway();
        }
        public ManagedPluginConnectionGateway CreateGateway() => new(Http, protection, Microsoft.Extensions.Options.Options.Create(new PluginConnectionOptions { Managed = Options }));
        public void Dispose() { Http.Dispose(); if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
    private sealed class ProofService : HttpMessageHandler, IHttpClientFactory
    {
        public ManagedPairing? Pairing;
        public DeploymentRegistration Registration = new(Guid.NewGuid(), "tenant-one", 1);
        public int Requests; public bool Fail;
        public Guid? CleanupAttempt;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            using var key = DeploymentProofCodec.PublicKey(Pairing!.PublicKey);
            var proof = DeploymentProofCodec.Verify(request.Headers.GetValues(ConnectionProtocol.ProofHeader).Single(), key);
            Assert.Equal(Registration.DeploymentId, proof.DeploymentId); Assert.Equal(Pairing.KeyId, proof.KeyId);
            Assert.Equal("https://service.example.com", proof.Audience);
            if (CleanupAttempt is { } attempt)
            {
                Assert.Equal("POST", proof.Method); Assert.Equal($"/v1/attempts/{attempt:D}/cancel", proof.PathAndQuery);
                var cancelled = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new ConnectionReceipt(Guid.NewGuid(), null, "Cancelled", null, []), options: DeploymentProofCodec.Json) };
                cancelled.Headers.Add("X-CSweet-Connection-Protocol", ConnectionProtocol.Version);
                return Task.FromResult(cancelled);
            }
            Assert.Equal("GET", proof.Method); Assert.Equal("/v1/deployment", proof.PathAndQuery);
            var response = new HttpResponseMessage(Fail ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = JsonContent.Create(Registration, options: DeploymentProofCodec.Json) };
            response.Headers.Add("X-CSweet-Connection-Protocol", ConnectionProtocol.Version);
            return Task.FromResult(response);
        }
    }
}
