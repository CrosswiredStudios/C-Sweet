using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Domain.Setup;
using CSweet.ExecutionGateway;

namespace CSweet.UnitTests;

public sealed class OfficeConnectionIdentityTests
{
    [Fact]
    public void RotationPreservesAuthenticatedConnectionButCannotAuthenticateNewConnectionsWithOldCertificate()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create();
        using var old = new CertificateRequest("CN=Office", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
        using var replacement = new CertificateRequest("CN=Office", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        var node = new ExecutionNode
        {
            Id = Guid.NewGuid(), ApprovedAt = now, Status = ExecutionNodeStatus.Ready,
            CertificateThumbprint = old.Thumbprint, CertificateSerialNumber = old.SerialNumber,
            CertificateExpiresAt = now.AddHours(1), CertificateSigningRequestPem = "original-enrolled-key"
        };
        var connection = new Dictionary<object, object?>();
        Assert.True(OfficeConnectionIdentity.Authorize(node, old, connection, now));
        node.CertificateThumbprint = replacement.Thumbprint;
        node.CertificateSerialNumber = replacement.SerialNumber;
        node.CertificateExpiresAt = now.AddDays(1);
        Assert.True(OfficeConnectionIdentity.Authorize(node, old, connection, now.AddHours(2)));
        Assert.False(OfficeConnectionIdentity.Authorize(node, old, new Dictionary<object, object?>(), now));
        Assert.True(OfficeConnectionIdentity.Authorize(node, replacement, new Dictionary<object, object?>(), now));
        node.Status = ExecutionNodeStatus.Revoked;
        Assert.False(OfficeConnectionIdentity.Authorize(node, old, connection, now));
        node.Status = ExecutionNodeStatus.Draining;
        Assert.True(OfficeConnectionIdentity.Authorize(node, old, connection, now));
        node.CertificateSigningRequestPem = "replaced-enrolled-key";
        Assert.False(OfficeConnectionIdentity.Authorize(node, old, connection, now));
    }

    [Fact]
    public void ExpiryWithoutRenewalAndLossOfApprovalInvalidateExistingConnections()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=Office", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddMinutes(-5), now.AddMinutes(5));
        var node = new ExecutionNode
        {
            Id = Guid.NewGuid(), ApprovedAt = now, Status = ExecutionNodeStatus.Ready,
            CertificateThumbprint = certificate.Thumbprint, CertificateSerialNumber = certificate.SerialNumber,
            CertificateExpiresAt = now.AddMinutes(5)
        };
        var connection = new Dictionary<object, object?>();
        Assert.True(OfficeConnectionIdentity.Authorize(node, certificate, connection, now));
        Assert.False(OfficeConnectionIdentity.Authorize(node, certificate, connection, now.AddMinutes(5)));
        node.ApprovedAt = null;
        Assert.False(OfficeConnectionIdentity.Authorize(node, certificate, connection, now));
    }
    [Fact]
    public async Task KestrelKeepsIdentityBoundToOneHttp2TlsConnectionAcrossRenewal()
    {
        var keyName = $"csweet-connection-test-{Guid.NewGuid():N}";
        using var windowsKey = OperatingSystem.IsWindows() ? CngKey.Create(CngAlgorithm.Rsa, keyName,
            new CngKeyCreationParameters { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport,
                KeyUsage = CngKeyUsages.AllUsages }) : null;
        try
        {
            using RSA key = OperatingSystem.IsWindows() ? new RSACng(windowsKey!) : RSA.Create(2048);
            var now = DateTimeOffset.UtcNow;
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var original = request.CreateSelfSigned(now.AddMinutes(-1), now.AddHours(1));
            using var replacement = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
            var node = new ExecutionNode { Id = Guid.NewGuid(), ApprovedAt = now, Status = ExecutionNodeStatus.Ready,
                CertificateThumbprint = original.Thumbprint, CertificateSerialNumber = original.SerialNumber,
                CertificateExpiresAt = now.AddHours(1), CertificateSigningRequestPem = "enrolled-key" };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(original, https =>
                {
                    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    https.ClientCertificateValidation = (_, _, _) => true;
                });
            }));
            await using var app = builder.Build();
            app.MapGet("/", (HttpContext context) =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature>();
                Assert.NotNull(feature);
                return OfficeConnectionIdentity.Authorize(node, context.Connection.ClientCertificate, feature.Items, now)
                    ? Results.Ok(context.Connection.Id) : Results.Unauthorized();
            });
            await app.StartAsync();
            try
            {
                HttpClient Client(X509Certificate2 certificate)
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == original.Thumbprint
                    };
                    handler.ClientCertificates.Add(certificate);
                    return new(handler) { BaseAddress = new Uri(app.Urls.Single()),
                        DefaultRequestVersion = HttpVersion.Version20,
                        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
                }
                using var existing = Client(original);
                var connectionId = await existing.GetStringAsync("/");
                node.CertificateThumbprint = replacement.Thumbprint;
                node.CertificateSerialNumber = replacement.SerialNumber;
                node.CertificateExpiresAt = now.AddDays(1);
                now = now.AddHours(2);
                Assert.Equal(connectionId, await existing.GetStringAsync("/"));
                using var stale = Client(original);
                Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/")).StatusCode);
                using var renewed = Client(replacement);
                Assert.Equal(HttpStatusCode.OK, (await renewed.GetAsync("/")).StatusCode);
                node.Status = ExecutionNodeStatus.Revoked;
                Assert.Equal(HttpStatusCode.Unauthorized, (await existing.GetAsync("/")).StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally { windowsKey?.Delete(); }
    }

}
