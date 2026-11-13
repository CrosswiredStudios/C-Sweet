using System.Security.Cryptography;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>
/// Accepts staged evidence parts from the provider that holds the live dispatch of an evidence-export operation. A part
/// must sit at a part boundary of the pinned archive, have exactly the expected length and match the SHA-256 the node
/// signed. Storing the same part again is idempotent; different bytes for a stored part are refused. The whole archive
/// is verified against its pinned SHA-256 only when the signed result arrives (<see cref="ComputeEvidenceParts.VerifyAsync"/>).
/// </summary>
public sealed class ComputeEvidencePartAccess(CSweetDbContext db, ComputeProviderWorkRequestVerifier verifier,
    ComputeBroker broker, TimeProvider clock)
{
    public async Task<ComputeEvidencePartReceipt> WriteAsync(ComputeEvidencePartUpload upload, CancellationToken token)
    {
        if (upload.Request is null || upload.Content is null) throw new UnauthorizedAccessException("An evidence part requires a signed request and content.");
        var fence = await ComputeDispatchFence.AuthorizeAsync(db, verifier, broker, clock, upload.Request, evidencePart: true, token);
        var archive = fence.Workload.EvidenceArchive ?? throw ComputeDispatchFence.Denied();
        int expected;
        try { expected = archive.PartLength(fence.Offset); }
        catch (ArgumentOutOfRangeException) { throw new UnauthorizedAccessException("The evidence part offset is not a part boundary of the pinned archive."); }
        var sha = Convert.ToHexStringLower(SHA256.HashData(upload.Content));
        if (upload.Content.Length != expected || sha != fence.Proof.InputSha256)
            throw new UnauthorizedAccessException("The evidence part differs from its signed digest or expected length.");
        var stored = await db.ComputeEvidenceParts.AsNoTracking().Where(x => x.OperationId == fence.Operation.Id && x.Offset == fence.Offset)
            .Select(x => x.Sha256).SingleOrDefaultAsync(token);
        if (stored is null)
        {
            db.ComputeEvidenceParts.Add(new ComputeEvidencePart
            {
                OperationId = fence.Operation.Id, Offset = fence.Offset, OrganizationId = fence.Operation.OrganizationId,
                Length = upload.Content.Length, Sha256 = sha, Content = upload.Content, CreatedAt = clock.GetUtcNow()
            });
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException)
            {
                // A concurrent retry of the same part may have stored it first; anything else is a real failure.
                db.ChangeTracker.Clear();
                if (await StoredShaAsync(fence.Operation.Id, fence.Offset, token) != sha) throw;
            }
        }
        else if (stored != sha) throw new InvalidOperationException("A different evidence part is already stored at this offset.");
        return new(fence.Operation.Id, fence.DispatchId, fence.Offset, sha);
    }

    private Task<string?> StoredShaAsync(Guid operationId, int offset, CancellationToken token) =>
        db.ComputeEvidenceParts.AsNoTracking().Where(x => x.OperationId == operationId && x.Offset == offset)
            .Select(x => x.Sha256).SingleOrDefaultAsync(token);
}

/// <summary>Reading and verifying an operation's staged evidence parts, one part in memory at a time.</summary>
internal static class ComputeEvidenceParts
{
    /// <summary>Every part is present, contiguous and of the expected length, and together they hash to the pinned SHA-256.</summary>
    internal static async Task VerifyAsync(CSweetDbContext db, Guid operationId, ComputeEvidenceArchive archive, CancellationToken token)
    {
        var index = await db.ComputeEvidenceParts.AsNoTracking().Where(x => x.OperationId == operationId)
            .OrderBy(x => x.Offset).Select(x => new { x.Offset, x.Length }).ToListAsync(token);
        if (index.Count != archive.PartCount)
            throw new InvalidDataException("Staged evidence is incomplete.");
        for (var i = 0; i < index.Count; i++)
            if (index[i].Offset != i * ComputeEvidenceArchive.PartBytes || index[i].Length != archive.PartLength(index[i].Offset))
                throw new InvalidDataException("Staged evidence parts are not contiguous.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in index)
        {
            var content = await ReadPartAsync(db, operationId, part.Offset, token);
            if (content.Length != part.Length) throw new InvalidDataException("A staged evidence part changed.");
            hash.AppendData(content);
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != archive.Sha256)
            throw new InvalidDataException("Staged evidence does not match the pinned archive digest.");
    }

    internal static async Task<byte[]> ReadPartAsync(CSweetDbContext db, Guid operationId, int offset, CancellationToken token) =>
        await db.ComputeEvidenceParts.AsNoTracking().Where(x => x.OperationId == operationId && x.Offset == offset)
            .Select(x => x.Content).SingleOrDefaultAsync(token) ?? throw new InvalidDataException("A staged evidence part is missing.");
}
