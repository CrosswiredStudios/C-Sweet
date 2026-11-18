using System.Security.Cryptography;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeSharedImageStoreTests
{
    [Fact]
    public async Task Businesses_reuse_one_base_image_and_retirement_leaves_it_available()
    {
        using var f = new Fixture();
        var firstBusiness = Directory.CreateDirectory(Path.Combine(f.Root, "business-one")).FullName;
        var secondBusiness = Directory.CreateDirectory(Path.Combine(f.Root, "business-two")).FullName;
        var first = Path.Combine(firstBusiness, "legacy.vhdx");
        var second = Path.Combine(secondBusiness, "download.vhdx");
        File.Copy(f.Source, first); File.Copy(f.Source, second);
        var shared = await f.Store().ImportAsync(first, f.Digest, default);
        // An open reader prevents overwriting the shared file, but a second setup can reuse it.
        using (var reader = File.OpenRead(shared))
            Assert.Equal(shared, await f.Store().ImportAsync(second, f.Digest, default));
        Assert.Single(Directory.GetFiles(f.Images));
        Assert.True(File.Exists(first)); // Migration retains the previous image for rollback.
        Directory.Delete(firstBusiness, recursive: true);
        Assert.Equal(shared, await f.Store().ImportAsync(second, f.Digest, default));
        Assert.Equal(File.ReadAllBytes(second), File.ReadAllBytes(shared));
    }

    [Fact]
    public async Task Different_image_versions_coexist_without_mutating_a_published_base()
    {
        using var f = new Fixture();
        var old = await f.Store().ImportAsync(f.Source, f.Digest, default);
        await File.WriteAllTextAsync(f.Source, "next image version");
        var current = await f.Store().ImportAsync(f.Source, Fixture.Hash(f.Source), default);
        Assert.NotEqual(old, current);
        Assert.Equal("base image", await File.ReadAllTextAsync(old));
        Assert.Equal("next image version", await File.ReadAllTextAsync(current));
    }

    [Fact]
    public async Task Concurrent_publishers_converge_on_one_verified_file()
    {
        using var f = new Fixture();
        using var staged = new Barrier(2);
        void Protect(string _) => Assert.True(staged.SignalAndWait(TimeSpan.FromSeconds(20)));
        var imports = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            f.Store(protect: Protect).ImportAsync(f.Source, f.Digest, default))));
        Assert.Equal(imports[0], imports[1]);
        Assert.Equal(f.Digest, Fixture.Hash(imports[0]));
        Assert.Single(Directory.GetFiles(f.Images));
    }

    [Fact]
    public async Task Changed_source_is_not_published_and_retry_can_succeed()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(f.Source, "changed after preparation");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store().ImportAsync(f.Source, f.Digest, default));
        Assert.Empty(Directory.GetFiles(f.Images));
        await File.WriteAllTextAsync(f.Source, "base image");
        Assert.Equal(f.Digest, Fixture.Hash(await f.Store().ImportAsync(f.Source, f.Digest, default)));
    }

    [Fact]
    public async Task Corrupt_shared_image_fails_closed_without_overwriting_other_businesses_data()
    {
        using var f = new Fixture();
        var shared = await f.Store().ImportAsync(f.Source, f.Digest, default);
        await File.WriteAllTextAsync(shared, "corrupt retained file");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store().ImportAsync(f.Source, f.Digest, default));
        Assert.Equal("corrupt retained file", await File.ReadAllTextAsync(shared));
        Assert.Single(Directory.GetFiles(f.Images));
    }

    [Fact]
    public async Task Cancellation_removes_only_own_staging_and_preserves_published_versions()
    {
        using var f = new Fixture();
        var old = await f.Store().ImportAsync(f.Source, f.Digest, default);
        await File.WriteAllTextAsync(f.Source, "new image");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Store(protect: _ => cancellation.Cancel()).ImportAsync(f.Source, Fixture.Hash(f.Source), cancellation.Token));
        Assert.Equal(new[] { old }, Directory.GetFiles(f.Images));
        Assert.Equal(f.Digest, Fixture.Hash(old));
    }

    [Fact]
    public async Task Unprotected_existing_image_is_never_reused()
    {
        using var f = new Fixture();
        var shared = await f.Store().ImportAsync(f.Source, f.Digest, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Store(verify: path =>
        {
            if (path == shared) throw new UnauthorizedAccessException("Untrusted ACL or redirected file");
        }).ImportAsync(f.Source, f.Digest, default));
        Assert.Equal(f.Digest, Fixture.Hash(shared));
    }

    [Theory]
    [InlineData("sha256:../../provider.json")]
    [InlineData("SHA256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Invalid_digest_cannot_select_a_store_path(string digest)
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store().ImportAsync(f.Source, digest, default));
        Assert.False(Directory.Exists(f.Images));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repair_preserves_enrolled_identity_and_features_when_migrating_or_reusing(bool alreadyShared)
    {
        using var f = new Fixture();
        var source = alreadyShared ? await f.Store().ImportAsync(f.Source, f.Digest, default) : f.Source;
        var template = new ComputeTemplate("linux-local-" + f.Digest[7..31], "linux", "x64", f.Digest,
            ["guest-execution", "browser-testing-v1"]);
        var document = new ComputeTemplateCatalogDocument(1, [new(template, source, null!)]);
        var retained = LocalComputeInstaller.PreserveEnrolledTemplate(document, source, template with { Features = [] });
        var shared = await f.Store().ImportAsync(source, retained.ImageDigest, default);
        Assert.Same(template, retained);
        Assert.Equal(f.Images, Path.GetDirectoryName(shared));
        Assert.True(File.Exists(source));
        Assert.Throws<InvalidOperationException>(() => LocalComputeInstaller.PreserveEnrolledTemplate(
            document, source + ".different", template));
        Assert.Throws<InvalidOperationException>(() => LocalComputeInstaller.PreserveEnrolledTemplate(
            document, source, template with { ImageDigest = "sha256:" + new string('a', 64) }));
    }

    [Fact]
    public void Shared_store_is_outside_both_legacy_and_business_retirement_roots()
    {
        foreach (var installation in new[] { new ComputeLocalInstallation(null), new ComputeLocalInstallation(Guid.NewGuid()) })
            Assert.False(ComputeSharedImageStore.InstallationRoot.StartsWith(
                installation.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetDirectoryName(new ComputeLocalInstallation(null).Root),
            Path.GetDirectoryName(ComputeSharedImageStore.InstallationRoot));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "csweet-shared-images-" + Guid.NewGuid().ToString("N"));
        public string Images => Path.Combine(Root, "images");
        public string Source => Path.Combine(Root, "prepared.vhdx");
        public string Digest { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "base image");
            Digest = Hash(Source);
        }
        public ComputeSharedImageStore Store(Action<string>? protect = null, Action<string>? verify = null) =>
            new(Images, path => Directory.CreateDirectory(path), protect ?? (_ => { }), verify ?? (_ => { }));
        public static string Hash(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
