using System.Security.Cryptography;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Artifacts;

public sealed record BuilderArtifactStreamGrant(
    Guid WorkloadId,
    Guid InstallationId,
    string StreamId,
    long MaximumBytes,
    string FormatVersion,
    string OperatingSystem,
    string Architecture,
    string ProvenanceJson);

/// <summary>
/// Receives exactly one ordered, length-limited artifact stream from an authenticated
/// builder guest. The host calculates the digest, validates the bundle through the
/// artifact store, signs the validated record, and only then publishes it to the build.
/// </summary>
public sealed class BuilderArtifactBrokerStreamHandler : IGuestBrokerStreamHandler, IAsyncDisposable
{
    private const int MaximumChunkBytes = 1024 * 1024;
    private readonly BuilderArtifactStreamGrant _grant;
    private readonly IAgentArtifactStore _artifacts;
    private readonly IBuilderArtifactResultPublisher _publisher;
    private readonly string _temporaryPath;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _nextSequence;
    private long _totalBytes;
    private bool _completed;

    public BuilderArtifactBrokerStreamHandler(
        BuilderArtifactStreamGrant grant,
        IAgentArtifactStore artifacts,
        IBuilderArtifactResultPublisher publisher,
        string temporaryRoot)
    {
        ValidateGrant(grant);
        if (string.IsNullOrWhiteSpace(temporaryRoot) || !Path.IsPathFullyQualified(temporaryRoot))
            throw new ArgumentException("The artifact staging root must be an absolute path.", nameof(temporaryRoot));
        _grant = grant;
        _artifacts = artifacts;
        _publisher = publisher;
        var root = Path.GetFullPath(temporaryRoot);
        if (Path.GetPathRoot(root) == root)
            throw new ArgumentException("The artifact staging root cannot be a filesystem root.", nameof(temporaryRoot));
        Directory.CreateDirectory(root);
        _temporaryPath = Path.Combine(root, $"{grant.WorkloadId:N}-{Guid.NewGuid():N}.stream");
        _stream = new FileStream(
            _temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
    }

    public async Task HandleAsync(GuestBrokerStreamContext chunk, CancellationToken cancellationToken)
    {
        if (_completed)
            throw new InvalidDataException("The builder artifact stream is already complete.");
        if (chunk.WorkloadId != _grant.WorkloadId || chunk.InstallationId != _grant.InstallationId ||
            !string.Equals(chunk.StreamId, _grant.StreamId, StringComparison.Ordinal) ||
            chunk.Sequence != _nextSequence)
            throw new InvalidDataException("The builder artifact stream identity or sequence is invalid.");
        if (chunk.Content.Length > MaximumChunkBytes)
            throw new InvalidDataException("The builder artifact chunk exceeds its limit.");

        _totalBytes = checked(_totalBytes + chunk.Content.Length);
        if (_totalBytes > _grant.MaximumBytes)
            throw new InvalidDataException("The builder artifact stream exceeds its byte limit.");
        if (chunk.Content.Length > 0)
        {
            _hash.AppendData(chunk.Content.Span);
            await _stream.WriteAsync(chunk.Content, cancellationToken);
        }
        _nextSequence++;

        if (!chunk.Completed)
        {
            if (chunk.Digest is not null)
                throw new InvalidDataException("Only the final builder artifact chunk may declare a digest.");
            return;
        }
        if (chunk.Digest is null)
            throw new InvalidDataException("The final builder artifact chunk must declare its digest.");

        var actualDigest = "sha256:" + Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(chunk.Digest, actualDigest, StringComparison.Ordinal))
            throw new InvalidDataException("The builder artifact digest does not match the streamed bytes.");
        await _stream.FlushAsync(cancellationToken);
        _stream.Position = 0;
        var artifact = await _artifacts.ImportAsync(
            _stream,
            new ArtifactImportDescriptor(
                actualDigest,
                _grant.MaximumBytes,
                _grant.FormatVersion,
                _grant.OperatingSystem,
                _grant.Architecture,
                _grant.ProvenanceJson),
            cancellationToken);
        await _publisher.PublishAsync(
            new BuilderArtifactResult(_grant.WorkloadId, artifact, $"artifact:{artifact.Digest}"),
            cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        await _stream.DisposeAsync();
        if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath);
    }

    private static void ValidateGrant(BuilderArtifactStreamGrant grant)
    {
        if (grant.WorkloadId == Guid.Empty || grant.InstallationId == Guid.Empty ||
            grant.StreamId.Length is < 3 or > 128 ||
            grant.StreamId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_') ||
            grant.MaximumBytes is < 1 or > 10L * 1024 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(grant.FormatVersion) || string.IsNullOrWhiteSpace(grant.OperatingSystem) ||
            string.IsNullOrWhiteSpace(grant.Architecture) || string.IsNullOrWhiteSpace(grant.ProvenanceJson))
            throw new ArgumentException("The builder artifact stream grant is invalid.", nameof(grant));
    }
}
