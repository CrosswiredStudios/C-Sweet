using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.UnitTests;

public sealed class PlatformRuntimePayloadManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "csweet-platform-payload-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidManifestAppliesOnlyDeclaredVerifiedProviderSettings()
    {
        var manifestPath = CreateManifest();
        var options = new PlatformIsolationBackendOptions { PayloadManifestPath = manifestPath };

        PlatformRuntimePayloadManifest.ApplyIfConfigured(
            options, IsolationProviderCatalog.Firecracker("test-arch"));

        Assert.Equal(Path.Combine(_root, "helper", "provider-helper"), options.HelperExecutablePath);
        Assert.StartsWith("sha256:", options.HelperExecutableDigest, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_root, "images", "guest.raw"), options.GuestImagePath);
        Assert.Equal("suite-1", options.CertificationSuiteVersion);
        Assert.Equal("1.0", options.BrokerProtocolVersion);
    }

    [Fact]
    public void ManifestRejectsAFileChangedAfterPackaging()
    {
        var manifestPath = CreateManifest();
        File.AppendAllText(Path.Combine(_root, "helper", "provider-helper"), "tampered");

        Assert.Throws<InvalidDataException>(() =>
            PlatformRuntimePayloadManifest.ApplyIfConfigured(
                new PlatformIsolationBackendOptions { PayloadManifestPath = manifestPath },
                IsolationProviderCatalog.Firecracker("test-arch")));
    }

    [Fact]
    public void ManifestRejectsWrongProviderAndTraversalPaths()
    {
        var manifestPath = CreateManifest();
        Assert.Throws<InvalidDataException>(() =>
            PlatformRuntimePayloadManifest.ApplyIfConfigured(
                new PlatformIsolationBackendOptions { PayloadManifestPath = manifestPath },
                IsolationProviderCatalog.AppleVirtualization("test-arch")));

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["helperExecutable"] = "../provider-helper";
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Assert.Throws<InvalidDataException>(() =>
            PlatformRuntimePayloadManifest.ApplyIfConfigured(
                new PlatformIsolationBackendOptions { PayloadManifestPath = manifestPath },
                IsolationProviderCatalog.Firecracker("test-arch")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string CreateManifest()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["helper/provider-helper"] = "helper",
            ["images/guest.raw"] = "guest",
            ["images/guest.raw.sig"] = "signature",
            ["certificates/guest-image.cer"] = "certificate",
            ["certification/firecracker.json"] = "evidence"
        };
        Directory.CreateDirectory(_root);
        var entries = new JsonArray();
        foreach (var file in files)
        {
            var path = Path.Combine(_root, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
            entries.Add(new JsonObject
            {
                ["path"] = file.Key,
                ["sha256"] = Sha256(path)
            });
        }

        var guestDigest = Sha256(Path.Combine(_root, "images", "guest.raw"));
        var evidenceDigest = Sha256(Path.Combine(_root, "certification", "firecracker.json"));
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["providerId"] = "firecracker-kvm",
            ["providerVersion"] = "1.0.0",
            ["hostOperatingSystem"] = "linux",
            ["hostArchitecture"] = "test-arch",
            ["helperExecutable"] = "helper/provider-helper",
            ["guestImage"] = "images/guest.raw",
            ["guestImageDigest"] = guestDigest,
            ["guestImageSignature"] = "images/guest.raw.sig",
            ["guestImageSigningCertificate"] = "certificates/guest-image.cer",
            ["guestImageSigningCertificateThumbprint"] = "001122",
            ["brokerProtocolVersion"] = "1.0",
            ["certificationSuiteVersion"] = "suite-1",
            ["certificationEvidence"] = "certification/firecracker.json",
            ["certificationEvidenceDigest"] = evidenceDigest,
            ["certifiedAt"] = "2026-08-11T00:00:00Z",
            ["files"] = entries
        };
        var manifestPath = Path.Combine(_root, "runtime-manifest.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return manifestPath;
    }

    private static string Sha256(string path) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";
}
