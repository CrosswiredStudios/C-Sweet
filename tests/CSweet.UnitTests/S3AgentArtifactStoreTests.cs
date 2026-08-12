using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.ExecutionArtifacts;

namespace CSweet.UnitTests;

public sealed class S3AgentArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "csweet-s3-artifact-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportUploadsValidatedContentAndReplayIsIdempotent()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var objects = new FakeObjectClient();
        var store = Store(objects);

        var first = await store.ImportAsync(new MemoryStream(bundle), Descriptor(digest));
        var second = await store.ImportAsync(new MemoryStream(bundle), Descriptor(digest));

        Assert.Equal(digest, first.Digest);
        Assert.Equal(first, second);
        Assert.Equal(1, objects.PutCount);
        Assert.True(await store.ExistsAsync(digest));
        await using var content = await store.OpenReadAsync(digest);
        Assert.Equal(bundle, await ReadAllAsync(content));
        Assert.StartsWith("tenant-a/sha256/", Assert.Single(objects.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReadRejectsWrongMetadataBeforeReturningContent()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var objects = new FakeObjectClient();
        var store = Store(objects);
        await store.ImportAsync(new MemoryStream(bundle), Descriptor(digest));
        objects.MetadataDigestOverride = "sha256:" + new string('0', 64);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenReadAsync(digest));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ExistsAsync(digest));
    }

    [Fact]
    public async Task DownloadRejectsTamperedContentAtEndOfStream()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var objects = new FakeObjectClient();
        var store = Store(objects);
        await store.ImportAsync(new MemoryStream(bundle), Descriptor(digest));
        objects.ContentMutator = bytes =>
        {
            var copy = bytes.ToArray();
            copy[^1] ^= 0x5a;
            return copy;
        };

        await using var content = await store.OpenReadAsync(digest);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(content));
    }

    [Fact]
    public async Task DownloadPropagatesInterruptedTransferWithoutAcceptingPartialData()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var objects = new FakeObjectClient();
        var store = Store(objects);
        await store.ImportAsync(new MemoryStream(bundle), Descriptor(digest));
        objects.InterruptAfterBytes = 64;

        await using var content = await store.OpenReadAsync(digest);
        await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(content));
    }

    [Fact]
    public async Task OversizedObjectMetadataIsRejected()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var objects = new FakeObjectClient();
        var store = Store(objects, maximumObjectBytes: bundle.Length - 1);
        var key = $"tenant-a/sha256/{digest[7..9]}/{digest[7..]}.csab";
        objects.Seed(key, bundle, digest);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenReadAsync(digest));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private S3AgentArtifactStore Store(FakeObjectClient objects, long maximumObjectBytes = 10 * 1024 * 1024) =>
        new(new S3ArtifactStoreOptions
        {
            BucketName = "test-artifacts",
            KeyPrefix = "tenant-a",
            MaximumObjectBytes = maximumObjectBytes
        }, objects, new FileSystemAgentArtifactStore(
            new ArtifactStoreOptions { RootPath = _root },
            new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32]))));

    private static ArtifactImportDescriptor Descriptor(string digest) => new(
        digest, 10 * 1024 * 1024, "1.0", "linux", "x64", "{}");

    private static byte[] Bundle()
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            Add(writer, "artifact.json",
                "{\"formatVersion\":\"1.0\",\"operatingSystem\":\"linux\",\"architecture\":\"x64\",\"entrypoint\":[\"agent\"]}");
            Add(writer, "payload/agent", new string('p', 1024), executable: true);
        }
        return output.ToArray();
    }

    private static void Add(TarWriter writer, string name, string content, bool executable = false)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Uid = 0,
            Gid = 0,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                UnixFileMode.OtherRead | (executable
                    ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0)
        };
        writer.WriteEntry(entry);
    }

    private static string Digest(byte[] content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<byte[]> ReadAllAsync(Stream input)
    {
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class FakeObjectClient : IS3ArtifactObjectClient
    {
        private readonly Dictionary<string, (byte[] Content, string Digest)> _objects = new(StringComparer.Ordinal);
        public int PutCount { get; private set; }
        public string? MetadataDigestOverride { get; set; }
        public Func<byte[], byte[]>? ContentMutator { get; set; }
        public int? InterruptAfterBytes { get; set; }
        public IReadOnlyCollection<string> Keys => _objects.Keys;

        public Task<S3ArtifactObjectMetadata?> GetMetadataAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_objects.TryGetValue(key, out var value)
                ? new S3ArtifactObjectMetadata(value.Content.Length, MetadataDigestOverride ?? value.Digest)
                : null);
        }

        public async Task PutAsync(
            string key,
            Stream content,
            long contentLength,
            string digest,
            CancellationToken cancellationToken = default)
        {
            var bytes = await ReadAllAsync(content);
            Assert.Equal(contentLength, bytes.Length);
            _objects[key] = (bytes, digest);
            PutCount++;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _objects[key].Content;
            bytes = ContentMutator?.Invoke(bytes) ?? bytes;
            Stream stream = new MemoryStream(bytes, writable: false);
            if (InterruptAfterBytes is { } limit) stream = new InterruptingStream(stream, limit);
            return Task.FromResult(stream);
        }

        public void Seed(string key, byte[] content, string digest) =>
            _objects[key] = (content, digest);
    }

    private sealed class InterruptingStream(Stream inner, int limit) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_read >= limit) throw new IOException("Simulated interrupted S3 transfer.");
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, limit - _read)], cancellationToken);
            _read += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }
}
