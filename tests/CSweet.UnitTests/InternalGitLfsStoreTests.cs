using System.Security.Cryptography;
using System.Text;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InternalGitLfsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-lfs-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _business = Guid.NewGuid(), _repository = Guid.NewGuid();
    private readonly InternalGitStorageOptions _options;
    public InternalGitLfsStoreTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".csweet-object-store"), "test");
        _options = new() { TemporaryRoot = Path.Combine(_root, "temporary"), Lfs = new() { RootPath = _root, ExpectedStoreId = "test" } };
    }

    [Fact]
    public async Task VerifiesAndRoundTripsRepositoryScopedObjects()
    {
        using var store = new InternalGitLfsStore(Options.Create(_options));
        var bytes = Encoding.UTF8.GetBytes("large binary asset");
        var oid = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await store.PutAsync(_business, _repository, oid, bytes.Length, new MemoryStream(bytes));
        using var result = new MemoryStream();
        await store.CopyToAsync(_business, _repository, oid, result);
        Assert.Equal(bytes, result.ToArray());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.CopyToAsync(Guid.NewGuid(), _repository, oid, new MemoryStream()));
    }

    [Fact]
    public async Task RejectsDigestMismatchOversizeAndMissingNasMarker()
    {
        using var store = new InternalGitLfsStore(Options.Create(_options));
        var bytes = Encoding.UTF8.GetBytes("asset");
        var oid = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutAsync(_business, _repository, new string('0', 64), bytes.Length, new MemoryStream(bytes)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutAsync(_business, _repository, oid, 1, new MemoryStream(bytes)));
        File.Delete(Path.Combine(_root, ".csweet-object-store"));
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(_business, _repository, oid, bytes.Length, new MemoryStream(bytes)));
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-lfs-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(_root, true);
    }
}
