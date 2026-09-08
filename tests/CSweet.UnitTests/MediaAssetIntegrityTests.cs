using System.Security.Cryptography;
using CSweet.Application.GenAi;
using CSweet.Domain.Core;
using CSweet.Infrastructure.GenAi;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed class MediaAssetIntegrityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(MediaIntegrityReadStream.ChunkBytes)]
    [InlineData(MediaIntegrityReadStream.ChunkBytes + 37)]
    public async Task IngestionProofsHashExactStreamAndBoundaries(int size)
    {
        var bytes = new byte[size]; Random.Shared.NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        using var stream = new MediaIntegrityReadStream(source);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Throws<InvalidOperationException>(() => stream.Complete());
        await stream.CopyToAsync(Stream.Null, 70_000);
        var proof = stream.Complete();
        Assert.Equal(size, proof.Size);
        Assert.Equal(Hash(bytes), proof.Sha256);
        long offset = 0;
        foreach (var chunk in proof.Chunks)
        {
            Assert.Equal(offset, chunk.Offset);
            Assert.Equal(Hash(bytes.AsSpan((int)offset, chunk.Length)), chunk.Sha256);
            offset += chunk.Length;
        }
        Assert.Equal(size, offset);
        Assert.Equal((size + MediaIntegrityReadStream.ChunkBytes - 1) / MediaIntegrityReadStream.ChunkBytes, proof.Chunks.Count);
        Assert.Equal(0, stream.Read(new byte[1], 0, 1));
        stream.Dispose();
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task ResumedUnalignedRangeVerifiesBothOriginalChunks()
    {
        await using var db = Database();
        var org = await Organization(db);
        var store = new Store();
        var service = new MediaAssetService(db, store);
        var bytes = Video(MediaIntegrityReadStream.ChunkBytes + 37);
        var asset = await service.SaveUploadAsync(org, "video.mp4", "video/mp4", new MemoryStream(bytes));
        var binding = new ConnectorMediaBinding(asset.Id, asset.Sha256, asset.SizeBytes, asset.ContentType);
        await MediaAssetIntegrity.RequireProofsAsync(db, binding, default);
        var offset = MediaIntegrityReadStream.ChunkBytes - 3;
        var range = await MediaAssetIntegrity.ReadRangeAsync(db, service, org, binding, offset, 6, default);
        Assert.Equal(bytes.AsSpan(offset, 6).ToArray(), range);
        store.Bytes[^1] ^= 1; // Outside the requested range, but inside its second integrity chunk.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            MediaAssetIntegrity.ReadRangeAsync(db, service, org, binding, offset, 6, default));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("length")]
    [InlineData("missing")]
    public async Task InconsistentProofsCannotAuthorizeBytes(string tamper)
    {
        await using var db = Database();
        var org = await Organization(db);
        var store = new Store();
        var service = new MediaAssetService(db, store);
        var asset = await service.SaveUploadAsync(org, "video.mp4", "video/mp4", new MemoryStream(Video(128)));
        var binding = new ConnectorMediaBinding(asset.Id, asset.Sha256, asset.SizeBytes, asset.ContentType);
        var proof = await db.MediaAssetChunks.SingleAsync();
        if (tamper == "root") proof.AssetSha256 = new string('0', 64);
        else if (tamper == "length") proof.Length--;
        else db.Remove(proof);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MediaAssetIntegrity.RequireProofsAsync(db, binding, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            MediaAssetIntegrity.ReadRangeAsync(db, service, org, binding, 0, 128, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageMustConsumeAllBytesAndReturnTheirRealDigest(bool incomplete)
    {
        await using var db = Database();
        var org = await Organization(db);
        var store = new Store { Incomplete = incomplete, WrongHash = !incomplete };
        var service = new MediaAssetService(db, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveUploadAsync(org, "video.mp4", "video/mp4", new MemoryStream(Video(128))));
        Assert.Empty(await db.MediaAssets.ToListAsync());
        Assert.Empty(await db.MediaAssetChunks.ToListAsync());
        Assert.True(store.Deleted);
    }

    [Fact]
    public void MigrationAddsOnlyHostIntegrityRecordsWithAssetCascade()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_script_only").Options);
        var sql = db.GetService<IMigrator>().GenerateScript("20260908000100_AddPluginOperationalStateAvailability",
            "20260908004653_AddMediaAssetChunkIntegrity", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE \"MediaAssetChunks\"", sql);
        Assert.Contains("PRIMARY KEY (\"MediaAssetId\", \"Offset\")", sql);
        Assert.Contains("ON DELETE CASCADE", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static byte[] Video(int size)
    {
        var bytes = new byte[size]; Random.Shared.NextBytes(bytes);
        "ftyp"u8.CopyTo(bytes.AsSpan(4)); return bytes;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Guid> Organization(CSweetDbContext db)
    {
        var id = Guid.NewGuid(); db.CoreOrganizations.Add(new Organization { Id = id, Name = "Test" });
        await db.SaveChangesAsync(); return id;
    }
    private sealed class Store : IMediaAssetStore
    {
        public byte[] Bytes { get; private set; } = [];
        public bool WrongHash, Incomplete, Deleted;
        public async Task<(string StorageKey, long SizeBytes, string Sha256)> SaveAsync(string fileName, Stream content, CancellationToken ct = default)
        {
            using var output = new MemoryStream();
            if (Incomplete)
            {
                var bytes = new byte[64]; var count = await content.ReadAsync(bytes, ct); output.Write(bytes, 0, count);
            }
            else await content.CopyToAsync(output, ct);
            Bytes = output.ToArray();
            return ("stored", Bytes.Length, WrongHash ? new string('0', 64) : Hash(Bytes));
        }
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(Bytes, false));
        public Task DeleteAsync(string storageKey, CancellationToken ct = default) { Deleted = true; return Task.CompletedTask; }
    }
}
