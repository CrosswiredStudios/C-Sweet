using System.Security.Cryptography;
namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Non-seekable input verification, including bytes beyond the TAR end marker.</summary>
internal sealed class BoundedDigestReadStream(Stream source, long maximumBytes) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long consumed;
    private bool complete;
    public override bool CanRead => !complete && source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => consumed; set => throw new NotSupportedException(); }
    private void Account(ReadOnlySpan<byte> bytes)
    {
        if (complete) throw new InvalidOperationException("The artifact stream was already verified.");
        consumed = checked(consumed + bytes.Length);
        if (consumed > maximumBytes) throw new InvalidDataException("The source archive exceeds its read budget.");
        hash.AppendData(bytes);
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        Account(buffer.AsSpan(offset, read)); return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var read = await source.ReadAsync(buffer, token);
        Account(buffer.Span[..read]); return read;
    }
    public async Task VerifyCompleteAsync(string expected, CancellationToken token)
    {
        await CopyToAsync(Stream.Null, token);
        var actual = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
        complete = true;
        if (actual != expected) throw new InvalidDataException("The source archive no longer matches its immutable build digest.");
    }
    protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
