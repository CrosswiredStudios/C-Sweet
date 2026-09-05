using CSweet.Contracts.SourceControl;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<InternalGitLfsTransferResult> TransferLfsAsync(InternalGitLfsTransfer request, CancellationToken ct = default)
    {
        if (request.Size < 0 || request.Size > MaximumGitRequestBytes || request.Body.Length > MaximumGitRequestBytes || request.Operation is not ("upload" or "download"))
            throw new ArgumentException("Unsupported LFS transfer or object size.");
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        using var lfs = new InternalGitLfsStore(Options.Create(_options));
        if (request.Operation == "upload")
        {
            await using var input = new MemoryStream(request.Body, false);
            await lfs.PutAsync(request.OrganizationId, request.RepositoryId, request.Oid, request.Size, input, ct);
            return new([]);
        }
        using var output = new BoundedLfsBuffer();
        await lfs.CopyToAsync(request.OrganizationId, request.RepositoryId, request.Oid, output, ct);
        return new(output.ToArray());
    }
    private sealed class BoundedLfsBuffer : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length + buffer.Length > MaximumGitRequestBytes) throw new IOException("LFS transfer exceeds the client transport limit.");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
