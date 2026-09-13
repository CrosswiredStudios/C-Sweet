using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeTemplateCatalogTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("relative-image")]
    [InlineData("wrong-release")]
    [InlineData("omitted-runtime")]
    [InlineData("unprotected")]
    public async Task Installed_catalog_requires_protected_unique_release_certified_entries(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var entry = new ComputeTemplateCatalogEntry(f.Packet.Template!,
            scenario == "relative-image" ? "clean.vhdx" : f.Image, f.Certification);
        var path = Path.Combine(f.Journal.Root, "templates.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ComputeTemplateCatalogDocument(1,
            scenario == "duplicate" ? [entry, entry] : [entry]), ComputeProtocol.Json));
        var required = new HashSet<string> { "runtime.dll" };
        if (scenario == "omitted-runtime") required.Add("missing.dll");
        var checkedPaths = new List<string>();
        void Guard(string value)
        {
            checkedPaths.Add(value);
            if (scenario == "unprotected") throw new UnauthorizedAccessException();
        }
        Task<ComputeTemplateCatalog> Read() => ComputeTemplateCatalog.ReadAsync(path, f.ReleasePublicKey,
            f.Claim.ProviderId, scenario == "wrong-release" ? "other-version" : "test-version",
            f.Payloads, required, f.Journal.Core.Time, Guard);
        if (scenario is "duplicate" or "relative-image") await Assert.ThrowsAsync<InvalidDataException>(Read);
        else if (scenario != "valid") await Assert.ThrowsAsync<UnauthorizedAccessException>(Read);
        else
        {
            var catalog = await Read();
            Assert.Contains(path, checkedPaths); Assert.Contains(f.Image, checkedPaths);
            // Detached metadata and a copied runtime manifest cannot alter later verification.
            catalog.Templates[entry.Template.Id].Features.Add("tampered"); required.Clear();
            using var payload = await catalog.OpenAsync(f.Verifier.Verify(f.Packet), default);
            Assert.Equal(f.Image, payload.ImagePath);
            Assert.Equal("Running", (await f.Executor(catalog).ExecuteAsync(f.Packet, default)).State);
            Assert.Equal(InfrastructureActions.Start, Assert.Single(f.Runner.Actions));
        }
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("image-changed")]
    [InlineData("runtime-changed")]
    public async Task Loaded_catalog_revalidates_certification_and_materialized_content_on_use(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var path = Path.Combine(f.Journal.Root, "templates.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ComputeTemplateCatalogDocument(1,
            [new(f.Packet.Template!, f.Image, f.Certification)]), ComputeProtocol.Json));
        var catalog = await ComputeTemplateCatalog.ReadAsync(path, f.ReleasePublicKey, f.Claim.ProviderId,
            "test-version", f.Payloads, new HashSet<string> { "runtime.dll" }, f.Journal.Core.Time, _ => { });
        var dispatch = f.Verifier.Verify(f.Packet);
        if (scenario == "expired") f.Journal.Core.Time.Now = f.Journal.Core.Time.Now.AddHours(1);
        else await File.AppendAllTextAsync(scenario == "image-changed" ? f.Image : Path.Combine(f.Payloads, "runtime.dll"), "changed");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.OpenAsync(dispatch, default));
    }
}
