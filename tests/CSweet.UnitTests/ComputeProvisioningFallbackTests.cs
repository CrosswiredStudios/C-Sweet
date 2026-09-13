using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeProvisioningFallbackTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("untrusted")]
    [InlineData("malformed")]
    [InlineData("cancelled")]
    [InlineData("disabled")]
    public async Task Rejected_catalog_disables_provisioning_without_hiding_shutdown(string scenario)
    {
        await using var f = new ComputeReplayJournalTests.Fixture(); await f.InitializeAsync();
        var config = new ComputeProviderConfiguration(1, f.Enrollment, f.Enrollment.ControlPlaneKey,
            "https://core.example.test", f.Root, Path.Combine(f.Root, "work"), new(10, new(40, 81920, 409600)),
            new string('A', 40), StoreLocation.LocalMachine, scenario == "disabled" ? null : "unused-settings-path");
        var codes = new List<string>(); var reads = 0;
        Task<ComputeTemplateCatalog> Read(string _, ComputeProviderConfiguration provider, TimeProvider clock, CancellationToken token)
        {
            reads++;
            throw scenario switch
            {
                "untrusted" => new UnauthorizedAccessException("private detail"),
                "malformed" => new JsonException("private detail"),
                "cancelled" => new OperationCanceledException(),
                _ => new FileNotFoundException("private detail")
            };
        }
        Task<ComputeTemplateCatalog?> Load() => HyperVMaintenanceHost.LoadCatalogAsync(config, f.Core.Time, codes.Add, default, Read);
        if (scenario == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(Load);
        else Assert.Null(await Load());
        Assert.Equal(scenario == "disabled" ? 0 : 1, reads);
        if (scenario is "disabled" or "cancelled") Assert.Empty(codes);
        else Assert.Equal("provisioning-configuration-unavailable", Assert.Single(codes));
    }
}
