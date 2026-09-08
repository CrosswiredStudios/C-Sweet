using System.Security.Cryptography;
using CSweet.Application.GenAi;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.GenAi;

/// <summary>Verifies ingested byte proofs before any approved range leaves the host.</summary>
public static class MediaAssetIntegrity
{
    public static async Task RequireProofsAsync(CSweetDbContext db, ConnectorMediaBinding media, CancellationToken ct)
    {
        var proofs = await db.MediaAssetChunks.AsNoTracking().Where(x => x.MediaAssetId == media.AssetId)
            .OrderBy(x => x.Offset)
            .Take((int)(MediaAssetStorageOptions.AbsoluteMaximumFileSizeBytes / MediaIntegrityReadStream.ChunkBytes) + 1)
            .ToListAsync(ct);
        long expected = 0;
        foreach (var proof in proofs)
        {
            var length = (int)Math.Min(MediaIntegrityReadStream.ChunkBytes, media.SizeBytes - expected);
            if (proof.Offset != expected || proof.Length != length || length <= 0 || proof.AssetSha256 != media.Sha256 ||
                proof.Sha256.Length != 64 || !proof.Sha256.All(Uri.IsHexDigit))
                throw new UnauthorizedAccessException("The approved media byte proofs are inconsistent.");
            expected += length;
        }
        if (expected != media.SizeBytes || expected <= 0)
            throw new UnauthorizedAccessException("This media has no complete ingestion proofs. Upload the original file again.");
    }

    public static async Task<byte[]> ReadRangeAsync(CSweetDbContext db, IMediaAssetService assets,
        Guid organization, ConnectorMediaBinding media, long offset, int maximumLength, CancellationToken ct)
    {
        if (offset < 0 || offset >= media.SizeBytes || maximumLength is < 1 or > MediaIntegrityReadStream.ChunkBytes)
            throw new InvalidOperationException("The media range is invalid.");
        var length = (int)Math.Min(maximumLength, media.SizeBytes - offset);
        var first = offset / MediaIntegrityReadStream.ChunkBytes * MediaIntegrityReadStream.ChunkBytes;
        var end = offset + length;
        var opened = await assets.OpenReadAsync(media.AssetId, organization, ct)
            ?? throw new InvalidOperationException("The approved media asset is unavailable.");
        await using var stream = opened.Content;
        if (opened.Asset.Sha256 != media.Sha256 || opened.Asset.SizeBytes != media.SizeBytes ||
            opened.Asset.ContentType != media.ContentType || !stream.CanSeek || stream.Length != media.SizeBytes)
            throw new UnauthorizedAccessException("The approved media changed or cannot be safely resumed.");
        var result = new byte[length];
        for (var start = first; start < end; start += MediaIntegrityReadStream.ChunkBytes)
        {
            var proof = await db.MediaAssetChunks.AsNoTracking().SingleOrDefaultAsync(x =>
                x.MediaAssetId == media.AssetId && x.Offset == start, ct);
            var expectedLength = (int)Math.Min(MediaIntegrityReadStream.ChunkBytes, media.SizeBytes - start);
            if (proof is null || proof.AssetSha256 != media.Sha256 || proof.Length != expectedLength)
                throw new UnauthorizedAccessException("The approved media byte proof is unavailable or changed.");
            var bytes = new byte[expectedLength];
            stream.Position = start;
            await stream.ReadExactlyAsync(bytes, ct);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, proof.Sha256, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Stored media bytes no longer match the approved upload.");
            var copyStart = Math.Max(start, offset);
            var copyEnd = Math.Min(start + expectedLength, end);
            bytes.AsSpan((int)(copyStart - start), (int)(copyEnd - copyStart))
                .CopyTo(result.AsSpan((int)(copyStart - offset)));
        }
        return result;
    }
}
