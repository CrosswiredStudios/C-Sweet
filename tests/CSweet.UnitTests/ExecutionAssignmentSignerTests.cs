using System.Security.Cryptography;
using CSweet.ExecutionGateway;
using CSweet.SatelliteOffice.Contracts.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ExecutionAssignmentSignerTests
{
    [Fact]
    public void ConfiguredSignerProducesCompleteBoundAuthorization()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = Options.Create(new ExecutionGatewayOptions
        {
            AssignmentSigningPrivateKeyPkcs8Base64 = Convert.ToBase64String(key.ExportPkcs8PrivateKey())
        });
        using var signer = new ExecutionAssignmentSigner(options, new EnvironmentStub("Production"));
        var office = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        var workload = Guid.NewGuid();
        var issued = DateTimeOffset.UtcNow;
        var expires = issued.AddMinutes(5);
        var digest = "sha256:" + new string('a', 64);
        var signature = signer.Sign(office, assignment, workload, 3, "hyperv-gen2", digest, issued, expires);

        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signer.ExportPublicKeyBase64()), out _);
        Assert.StartsWith("ecdsa-p256-sha256:", signer.KeyId, StringComparison.Ordinal);
        Assert.True(publicKey.VerifyData(
            AssignmentEnvelope.Payload(office, assignment, workload, 3, "hyperv-gen2", digest, issued, expires),
            signature, HashAlgorithmName.SHA256));
        Assert.False(publicKey.VerifyData(
            AssignmentEnvelope.Payload(office, assignment, workload, 3, "firecracker-kvm", digest, issued, expires),
            signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void ProductionRefusesAnEphemeralAssignmentKey()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ExecutionAssignmentSigner(Options.Create(new ExecutionGatewayOptions()),
                new EnvironmentStub("Production")));
    }

    private sealed class EnvironmentStub(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CSweet.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
