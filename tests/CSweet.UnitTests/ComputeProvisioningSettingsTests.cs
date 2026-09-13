using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeProvisioningSettingsTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("version")]
    [InlineData("catalog-in-workload")]
    [InlineData("runtime-in-journal")]
    [InlineData("wrong-key")]
    [InlineData("missing-runtime")]
    public async Task Installed_settings_bind_catalog_and_release_outside_mutable_roots(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var path = Path.Combine(f.Journal.Root, "provisioning.json");
        var catalogPath = Path.Combine(f.Journal.Root, "catalog.json");
        var provider = new ComputeProviderConfiguration(1, f.Journal.Enrollment, f.Journal.Enrollment.ControlPlaneKey,
            "https://core.example.test", Path.Combine(f.Journal.Root, "state"), f.Workloads, new(10, new(40, 81920, 409600)),
            new string('A', 40), StoreLocation.LocalMachine, path);
        var settings = new ComputeProvisioningSettings(1, catalogPath, f.ReleasePublicKey, "test-version",
            f.Payloads, ["runtime.dll"]);
        settings = scenario switch
        {
            "version" => settings with { Version = 0 },
            "catalog-in-workload" => settings with { CatalogPath = Path.Combine(f.Workloads, "catalog.json") },
            "runtime-in-journal" => settings with { RuntimeDirectory = provider.JournalDirectory },
            "wrong-key" => settings with { ReleasePublicKey = provider.Enrollment.ControlPlaneKey.PublicKeyBase64 },
            "missing-runtime" => settings with { RuntimeFiles = ["runtime.dll", "other.dll"] },
            _ => settings
        };
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new ComputeTemplateCatalogDocument(1,
            [new(f.Packet.Template!, f.Image, f.Certification)]), ComputeProtocol.Json));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, ComputeProtocol.Json));
        var checkedPaths = new List<string>();
        Task<ComputeTemplateCatalog> Read() => ComputeProvisioningSettingsLoader.ReadCatalogAsync(path, provider,
            f.Journal.Core.Time, checkedPaths.Add, default);
        if (scenario is "version" or "catalog-in-workload" or "runtime-in-journal")
            await Assert.ThrowsAsync<InvalidDataException>(Read);
        else if (scenario != "valid") await Assert.ThrowsAsync<UnauthorizedAccessException>(Read);
        else
        {
            var catalog = await Read();
            Assert.Contains(path, checkedPaths); Assert.Contains(catalogPath, checkedPaths);
            Assert.Equal("Running", (await f.Executor(catalog).ExecuteAsync(f.Packet, default)).State);
        }
    }
}
