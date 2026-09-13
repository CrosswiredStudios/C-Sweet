using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeProviderConfigurationTests
{
    [Fact]
    public async Task Protected_configuration_loads_without_opening_credentials_and_checks_all_roots()
    {
        await using var f = new ComputeReplayJournalTests.Fixture(); Directory.CreateDirectory(f.Root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = Configuration(f.Root, key);
        var path = Path.Combine(f.Root, "provider.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, ComputeProtocol.Json));
        var checkedPaths = new List<string>();
        Assert.Equal(config, await ComputeProviderConfigurationLoader.ReadAsync(path, checkedPaths.Add, default));
        Assert.Equal(new[] { path, config.JournalDirectory, config.WorkloadDirectory }, checkedPaths);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ComputeProviderConfigurationLoader.ReadAsync(path,
            _ => throw new UnauthorizedAccessException("Unprotected path."), default));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("nested")]
    [InlineData("scope")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("thumbprint")]
    public async Task Invalid_configuration_is_rejected_before_runtime_composition(string invalid)
    {
        await using var f = new ComputeReplayJournalTests.Fixture(); Directory.CreateDirectory(f.Root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = Configuration(f.Root, key);
        config = invalid switch
        {
            "endpoint" => config with { CoreOrigin = "https://user:secret@example.test/api" },
            "nested" => config with { WorkloadDirectory = Path.Combine(config.JournalDirectory, "work") },
            "scope" => config with { Enrollment = config.Enrollment with { NodeId = Guid.Empty } },
            "key" => config with { NodeSigningIdentity = new("node", "invalid") },
            "version" => config with { Version = 0 },
            _ => config with { CertificateThumbprint = "pick-any-certificate" }
        };
        var path = Path.Combine(f.Root, "provider.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, ComputeProtocol.Json));
        await Assert.ThrowsAsync<InvalidDataException>(() => ComputeProviderConfigurationLoader.ReadAsync(path, _ => { }, default));
    }

    [Fact]
    public async Task Unknown_fields_and_oversized_files_cannot_supply_hidden_configuration()
    {
        await using var f = new ComputeReplayJournalTests.Fixture(); Directory.CreateDirectory(f.Root);
        var path = Path.Combine(f.Root, "provider.json");
        await File.WriteAllTextAsync(path, "{\"privateKey\":\"unexpected\"}");
        await Assert.ThrowsAsync<JsonException>(() => ComputeProviderConfigurationLoader.ReadAsync(path, _ => { }, default));
        await File.WriteAllTextAsync(path, new string(' ', 65537));
        await Assert.ThrowsAsync<InvalidDataException>(() => ComputeProviderConfigurationLoader.ReadAsync(path, _ => { }, default));
    }

    [Fact]
    public async Task Certificate_selection_requires_exact_enrollment_current_validity_and_unique_private_key()
    {
        await using var f = new ComputeReplayJournalTests.Fixture(); Directory.CreateDirectory(f.Root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = new CertificateRequest("CN=provider-config-test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var config = Configuration(f.Root, key) with { CertificateThumbprint = certificate.Thumbprint };
        using var selected = ComputeProviderConfigurationLoader.SelectSigningCertificate(config, [certificate], TimeProvider.System);
        Assert.Equal(certificate.Thumbprint, selected.Thumbprint); Assert.True(selected.HasPrivateKey);
        Assert.Throws<InvalidOperationException>(() => ComputeProviderConfigurationLoader.SelectSigningCertificate(config, [], TimeProvider.System));
        Assert.Throws<InvalidOperationException>(() => ComputeProviderConfigurationLoader.SelectSigningCertificate(config, [certificate, certificate], TimeProvider.System));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidOperationException>(() => ComputeProviderConfigurationLoader.SelectSigningCertificate(
            config with { NodeSigningIdentity = Identity(otherKey) }, [certificate], TimeProvider.System));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        Assert.Throws<InvalidOperationException>(() => ComputeProviderConfigurationLoader.SelectSigningCertificate(config, [publicOnly], TimeProvider.System));
        using var expiring = new CertificateRequest("CN=provider-expiry-test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddSeconds(30));
        Assert.Throws<InvalidOperationException>(() => ComputeProviderConfigurationLoader.SelectSigningCertificate(
            config with { CertificateThumbprint = expiring.Thumbprint }, [expiring], TimeProvider.System));
    }

    [Fact]
    public async Task Maintenance_signing_rechecks_certificate_validity_after_service_start()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = f.Provider.Journal.Core.Time.Now;
        using var certificate = new CertificateRequest("CN=provider-signing-validity-test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddHours(1));
        var signer = new ComputeMaintenanceSigner(certificate, "enrolled-key", f.Provider.Journal.Core.Time);
        var row = (await f.Provider.Journal.Journal().ListMaintenanceEventsAsync(1, default))[0];
        Assert.NotNull(signer.Sign(row));
        f.Provider.Journal.Core.Time.Now = now.AddMinutes(59);
        Assert.Throws<InvalidOperationException>(() => signer.Sign(row));
    }
    private static ComputeProviderConfiguration Configuration(string root, ECDsa key) => new(1,
        new(Guid.NewGuid(), Guid.NewGuid(), "hyperv", Identity(key)), Identity(key), "https://core.example.test",
        Path.Combine(root, "journal"), Path.Combine(root, "workloads"), new(10, new(40, 81920, 409600)),
        new string('A', 40), StoreLocation.LocalMachine);

    private static ComputeSigningIdentity Identity(ECDsa key) => new("enrolled-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
}
