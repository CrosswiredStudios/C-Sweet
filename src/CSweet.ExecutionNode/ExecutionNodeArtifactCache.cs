using System.Security.Cryptography;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Artifacts;
using CSweet.AgentRuntime.Protocol;
using Grpc.Core;

namespace CSweet.ExecutionNode;

public sealed class ExecutionNodeArtifactCache : IAgentArtifactStore
{
    private readonly string _root;
    private readonly IAgentArtifactMediaStore _media;

    public ExecutionNodeArtifactCache(ExecutionNodeOptions options)
    {
        _root = options.ResolveArtifactCacheDirectory();
        Directory.CreateDirectory(_root);
        _media = new FileSystemAgentArtifactMediaStore(
            new ArtifactMediaOptions { RootPath = options.ResolveArtifactMediaDirectory() }, this);
    }

    public async Task EnsureAsync(
        ExecutionNodeGateway.ExecutionNodeGatewayClient client,
        ExecutionNodeState state,
        WorkloadAssignment assignment,
        string digest,
        CancellationToken cancellationToken)
    {
        ValidateDigest(digest);
        var path = PathFor(digest);
        if (!await VerifyAsync(path, digest, cancellationToken))
        {
            Directory.CreateDirectory(_root);
            var transferId = Guid.NewGuid().ToString("D");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var temporary = Path.Combine(_root, $".{Guid.NewGuid():N}.download");
                try
                {
                    using var call = client.DownloadArtifact(new ArtifactDownloadRequest
                    {
                        ProtocolVersion = "1.0",
                        NodeId = state.NodeId.ToString("D"),
                        SessionEpoch = state.SessionEpoch,
                        AssignmentId = assignment.AssignmentId,
                        FencingEpoch = assignment.FencingEpoch,
                        ArtifactDigest = digest,
                        ArtifactReadToken = assignment.ArtifactReadToken,
                        TransferId = transferId
                    }, cancellationToken: cancellationToken);
                    await using var output = new FileStream(
                        temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                    long expectedOffset = 0;
                    string? completedDigest = null;
                    await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
                    {
                        if (chunk.Offset != expectedOffset || chunk.Content.Length > 64 * 1024)
                            throw new InvalidDataException("The artifact transfer sequence is invalid.");
                        if (chunk.Content.Length > 0)
                        {
                            await output.WriteAsync(chunk.Content.Memory, cancellationToken);
                            expectedOffset += chunk.Content.Length;
                        }
                        if (expectedOffset > 2L * 1024 * 1024 * 1024)
                            throw new InvalidDataException("The artifact transfer exceeded the cache limit.");
                        if (chunk.Completed)
                        {
                            if (chunk.TotalSize != expectedOffset) throw new InvalidDataException("The artifact size did not match.");
                            completedDigest = chunk.Sha256;
                        }
                    }
                    await output.FlushAsync(cancellationToken);
                    if (!string.Equals(completedDigest, digest, StringComparison.Ordinal) ||
                        !await VerifyAsync(temporary, digest, cancellationToken))
                        throw new InvalidDataException("The downloaded artifact failed SHA-256 verification.");
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (RpcException exception) when (attempt < 3 &&
                    exception.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.DeadlineExceeded)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
        await _media.EnsureReadOnlyMediaAsync(digest, cancellationToken);
    }

    public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) =>
        VerifyAsync(PathFor(digest), digest, cancellationToken);

    public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDigest(digest);
        return Task.FromResult<Stream>(new FileStream(
            PathFor(digest), FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task<AgentArtifactReference> ImportAsync(
        Stream content, ArtifactImportDescriptor descriptor,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Execution nodes receive artifacts only through assignment-scoped grants.");

    private string PathFor(string digest) => Path.Combine(_root, $"{digest[7..]}.artifact");

    private static async Task<bool> VerifyAsync(string path, string digest, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = $"sha256:{Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken))}";
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(digest));
    }

    private static void ValidateDigest(string digest)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("The artifact digest is invalid.", nameof(digest));
    }
}
