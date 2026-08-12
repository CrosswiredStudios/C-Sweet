using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace CSweet.AgentRuntime.Artifacts;

public sealed record S3ArtifactObjectMetadata(long ContentLength, string Digest);

public interface IS3ArtifactObjectClient
{
    Task<S3ArtifactObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default);
    Task PutAsync(string key, Stream content, long contentLength, string digest, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class AmazonS3ArtifactObjectClient : IS3ArtifactObjectClient, IDisposable
{
    private readonly S3ArtifactStoreOptions _options;
    private readonly IAmazonS3 _client;

    public AmazonS3ArtifactObjectClient(S3ArtifactStoreOptions options)
    {
        options.Validate();
        _options = options;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region)
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
            config.ServiceURL = options.ServiceUrl;
        _client = string.IsNullOrWhiteSpace(options.AccessKeyId)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public async Task<S3ArtifactObjectMetadata?> GetMetadataAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.BucketName,
                Key = key
            }, cancellationToken);
            return new S3ArtifactObjectMetadata(
                response.ContentLength,
                response.Metadata["x-amz-meta-csweet-sha256"] ?? string.Empty);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task PutAsync(
        string key,
        Stream content,
        long contentLength,
        string digest,
        CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            ContentType = "application/vnd.csweet.agent-bundle",
            CannedACL = S3CannedACL.Private
        };
        request.Headers.ContentLength = contentLength;
        request.Metadata["csweet-sha256"] = digest;
        await _client.PutObjectAsync(request, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key
        }, cancellationToken);
        return new OwnedResponseStream(response);
    }

    public void Dispose() => _client.Dispose();

    private sealed class OwnedResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
