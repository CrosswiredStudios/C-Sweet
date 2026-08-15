using System.Security.Cryptography;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;

namespace CSweet.ExecutionArtifacts;

public sealed class S3AgentArtifactStore : IAgentArtifactStore
{
    private readonly S3ArtifactStoreOptions _options;
    private readonly IS3ArtifactObjectClient _objects;
    private readonly FileSystemAgentArtifactStore _staging;

    public S3AgentArtifactStore(
        S3ArtifactStoreOptions options,
        IS3ArtifactObjectClient objects,
        FileSystemAgentArtifactStore staging)
    {
        options.Validate();
        _options = options;
        _objects = objects;
        _staging = staging;
    }

    public async Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default)
    {
        var metadata = await _objects.GetMetadataAsync(Key(digest), cancellationToken);
        if (metadata is null) return false;
        ValidateMetadata(metadata, digest);
        return true;
    }

    public async Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default)
    {
        var metadata = await _objects.GetMetadataAsync(Key(digest), cancellationToken)
            ?? throw new FileNotFoundException("The requested artifact does not exist.", digest);
        ValidateMetadata(metadata, digest);
        var content = await _objects.OpenReadAsync(Key(digest), cancellationToken);
        return new DigestVerifyingReadStream(content, metadata.ContentLength, digest);
    }

    public async Task<AgentArtifactReference> ImportAsync(
        Stream content,
        ArtifactImportDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var artifact = await _staging.ImportAsync(content, descriptor, cancellationToken);
        var key = Key(artifact.Digest);
        var metadata = await _objects.GetMetadataAsync(key, cancellationToken);
        if (metadata is not null)
        {
            ValidateMetadata(metadata, artifact.Digest);
            return artifact;
        }

        await using var validated = await _staging.OpenReadAsync(artifact.Digest, cancellationToken);
        if (!validated.CanSeek)
            throw new InvalidOperationException("The validated artifact staging stream must expose its size.");
        await _objects.PutAsync(key, validated, validated.Length, artifact.Digest, cancellationToken);
        metadata = await _objects.GetMetadataAsync(key, cancellationToken)
            ?? throw new IOException("The S3-compatible store did not persist the artifact.");
        ValidateMetadata(metadata, artifact.Digest, validated.Length);
        return artifact;
    }

    private string Key(string digest)
    {
        ValidateDigest(digest);
        var hex = digest[7..].ToLowerInvariant();
        return $"{_options.KeyPrefix.TrimEnd('/')}/sha256/{hex[..2]}/{hex}.csab";
    }

    private void ValidateMetadata(S3ArtifactObjectMetadata metadata, string digest, long? expectedLength = null)
    {
        if (!string.Equals(metadata.Digest, digest, StringComparison.Ordinal) ||
            metadata.ContentLength < 0 || metadata.ContentLength > _options.MaximumObjectBytes ||
            expectedLength is not null && metadata.ContentLength != expectedLength)
            throw new InvalidDataException("The S3 artifact object metadata failed its integrity check.");
    }

    private static void ValidateDigest(string digest)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("Artifact digests must be lowercase SHA-256 identifiers.", nameof(digest));
    }

    private sealed class DigestVerifyingReadStream(
        Stream inner,
        long expectedLength,
        string expectedDigest) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _read;
        private bool _verified;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expectedLength;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Observe(buffer.AsSpan(offset, read), read == 0);
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Observe(buffer[..read], read == 0);
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Observe(buffer.Span[..read], read == 0);
            return read;
        }
        private void Observe(ReadOnlySpan<byte> content, bool completed)
        {
            if (_verified) return;
            if (!content.IsEmpty)
            {
                _read = checked(_read + content.Length);
                if (_read > expectedLength) throw new InvalidDataException("The S3 artifact exceeded its declared size.");
                _hash.AppendData(content);
            }
            if (!completed) return;
            var actual = "sha256:" + Convert.ToHexStringLower(_hash.GetHashAndReset());
            _verified = true;
            if (_read != expectedLength || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(expectedDigest)))
                throw new InvalidDataException("The S3 artifact content failed its integrity check.");
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _hash.Dispose(); inner.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            _hash.Dispose();
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
