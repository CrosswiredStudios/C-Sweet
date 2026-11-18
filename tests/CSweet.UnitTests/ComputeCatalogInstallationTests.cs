using System.Text.Json;
using CSweet.Compute.Runtime;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeCatalogInstallationTests
{
    [Fact]
    public async Task Independent_provider_catalogs_validate_and_open_the_same_shared_image()
    {
        await using var first = new Fixture(); await first.InitializeAsync();
        await using var second = new Fixture(); await second.InitializeAsync();
        var storeRoot = Path.Combine(first.Journal.Root, "shared-images");
        var store = new ComputeSharedImageStore(storeRoot, path => Directory.CreateDirectory(path), _ => { }, _ => { });
        var image = await store.ImportAsync(first.Image, first.Packet.Template!.ImageDigest, default);
        Assert.Equal(image, await store.ImportAsync(second.Image, second.Packet.Template!.ImageDigest, default));
        async Task<ComputeTemplateCatalog> Catalog(Fixture f)
        {
            File.Delete(f.Image); // Runtime directories contain only their certified executable files.
            var path = Path.Combine(f.Journal.Root, "catalog.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ComputeTemplateCatalogDocument(1,
                [new(f.Packet.Template!, image, f.Certification)]), ComputeProtocol.Json));
            var catalog = await ComputeTemplateCatalog.ReadAsync(path, f.ReleasePublicKey, f.Claim.ProviderId,
                "test-version", f.Payloads, new HashSet<string> { "runtime.dll" }, f.Journal.Core.Time, _ => { });
            await catalog.ValidateInstallationAsync(f.Payloads, default);
            return catalog;
        }
        var firstCatalog = await Catalog(first);
        var secondCatalog = await Catalog(second);
        using var firstPayload = await firstCatalog.OpenAsync(first.Verifier.Verify(first.Packet), default);
        using var secondPayload = await secondCatalog.OpenAsync(second.Verifier.Verify(second.Packet), default);
        Assert.Equal(firstPayload.ImagePath, secondPayload.ImagePath);
        Assert.NotEqual(first.Journal.Enrollment.OrganizationId, second.Journal.Enrollment.OrganizationId);
        Assert.NotEqual(first.Workloads, second.Workloads);
        Assert.Empty(first.Runner.Scripts);
        Assert.Empty(second.Runner.Scripts);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("extra-file")]
    [InlineData("nested-runtime")]
    [InlineData("different-directory")]
    [InlineData("changed-runtime")]
    [InlineData("changed-image")]
    public async Task Installation_checks_actual_runtime_coverage_and_all_materialized_payloads(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        // Keep certified image assets outside the flat executable payload directory.
        var image = Path.Combine(f.Journal.Root, "installed-clean.vhdx");
        File.Move(f.Image, image);
        var path = Path.Combine(f.Journal.Root, "catalog.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ComputeTemplateCatalogDocument(1,
            [new(f.Packet.Template!, image, f.Certification)]), ComputeProtocol.Json));
        var catalog = await ComputeTemplateCatalog.ReadAsync(path, f.ReleasePublicKey, f.Claim.ProviderId,
            "test-version", f.Payloads, new HashSet<string> { "runtime.dll" }, f.Journal.Core.Time, _ => { });
        if (scenario == "extra-file") await File.WriteAllTextAsync(Path.Combine(f.Payloads, "omitted.dll"), "extra");
        if (scenario == "nested-runtime") Directory.CreateDirectory(Path.Combine(f.Payloads, "nested"));
        if (scenario == "changed-runtime") await File.AppendAllTextAsync(Path.Combine(f.Payloads, "runtime.dll"), "changed");
        if (scenario == "changed-image") await File.AppendAllTextAsync(image, "changed");
        Task Validate() => catalog.ValidateInstallationAsync(scenario == "different-directory" ? f.Journal.Root : f.Payloads, default);
        if (scenario == "valid") await Validate();
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(Validate);
        // Validation creates no VM and releases all file handles, including on failure.
        Assert.Empty(f.Runner.Scripts);
        using var imageWrite = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.None);
        using var runtimeWrite = new FileStream(Path.Combine(f.Payloads, "runtime.dll"), FileMode.Open, FileAccess.Write, FileShare.None);
    }
}
