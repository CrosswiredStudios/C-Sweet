using System.Security.Cryptography;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.GenAi;

/// <summary>Hashes the exact forward-only bytes consumed by storage, without buffering the asset.</summary>
public sealed class MediaIntegrityReadStream(Stream source) : Stream
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    private readonly IncrementalHash _whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash _chunk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<MediaAssetChunk> _chunks = [];
    private long _total;
    private int _chunkLength;
    private bool _ended;
    private string? _sha;

    public (long Size, string Sha256, IReadOnlyList<MediaAssetChunk> Chunks) Complete()
    {
        if (!_ended) throw new InvalidOperationException("Storage did not consume the complete media stream.");
        return (_total, _sha!, _chunks);
    }
    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_ended)
        {
            if (!bytes.IsEmpty) throw new InvalidOperationException("Media changed after end of stream.");
            return;
        }
        if (bytes.IsEmpty)
        {
            if (_chunkLength > 0) FinishChunk();
            _sha = Convert.ToHexString(_whole.GetHashAndReset()).ToLowerInvariant();
            _ended = true; return;
        }
        if (_total > MediaAssetStorageOptions.AbsoluteMaximumFileSizeBytes - bytes.Length)
            throw new InvalidOperationException("Media exceeds the absolute file limit.");
        _whole.AppendData(bytes);
        while (!bytes.IsEmpty)
        {
            var take = Math.Min(ChunkBytes - _chunkLength, bytes.Length);
            _chunk.AppendData(bytes[..take]); _total += take; _chunkLength += take; bytes = bytes[take..];
            if (_chunkLength == ChunkBytes) FinishChunk();
        }
    }
    private void FinishChunk()
    {
        _chunks.Add(new() { Offset = _total - _chunkLength, Length = _chunkLength,
            Sha256 = Convert.ToHexString(_chunk.GetHashAndReset()).ToLowerInvariant() });
        _chunkLength = 0;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        var read = source.Read(buffer, offset, count); Append(buffer.AsSpan(offset, read)); return read;
    }
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        var read = source.Read(buffer); Append(buffer[..read]); return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;
        var read = await source.ReadAsync(buffer, ct); Append(buffer.Span[..read]); return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _total; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _whole.Dispose(); _chunk.Dispose(); }
        base.Dispose(disposing); // The caller owns the source stream.
    }
}
