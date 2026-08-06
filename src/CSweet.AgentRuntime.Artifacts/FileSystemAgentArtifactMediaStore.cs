using System.Collections.Concurrent;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.Artifacts;

public sealed class FileSystemAgentArtifactMediaStore(
    ArtifactMediaOptions options,
    IAgentArtifactStore artifacts) : IAgentArtifactMediaStore
{
    private readonly string _root = options.ValidatedRootPath();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task EnsureReadOnlyMediaAsync(
        string digest,
        CancellationToken cancellationToken = default)
    {
        ValidateDigest(digest);
        var gate = _locks.GetOrAdd(digest, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathForDigest(digest);
            if (File.Exists(path) &&
                await SingleFileIso9660.VerifyArtifactDigestAsync(path, digest, cancellationToken))
                return;
            Directory.CreateDirectory(_root);
            var temporary = Path.Combine(_root, $".{Guid.NewGuid():N}.iso.tmp");
            try
            {
                await using var artifact = await artifacts.OpenReadAsync(digest, cancellationToken);
                if (!artifact.CanSeek || artifact.Length is < 1 or > uint.MaxValue)
                    throw new InvalidDataException("The validated artifact cannot be represented by bounded ISO media.");
                await using (var output = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    await SingleFileIso9660.WriteAsync(artifact, artifact.Length, output, cancellationToken);
                }
                if (!await SingleFileIso9660.VerifyArtifactDigestAsync(temporary, digest, cancellationToken))
                    throw new InvalidDataException("Generated artifact media failed its integrity verification.");
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string PathForDigest(string digest) =>
        Path.Combine(_root, $"{digest[7..]}.iso");

    private static void ValidateDigest(string digest)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("The artifact digest is invalid.", nameof(digest));
    }
}
