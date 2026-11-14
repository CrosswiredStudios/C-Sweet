using System.Threading.Channels;

namespace CSweet.Mobile.Transport;

/// <summary>
/// A duplex byte stream over one relay channel. Writes become frames of at most <see cref="RelayProtocol.MaxFrameBytes"/>.
/// Incoming frames are buffered up to a fixed count; when full, delivery waits, which pushes back through the relay.
/// End of stream when the channel closes. The bytes are TLS records; this class never interprets them.
/// </summary>
public sealed class RelayChannelStream : Stream
{
    private readonly IRelayConnection _relay;
    private readonly Channel<byte[]> _incoming;
    private ReadOnlyMemory<byte> _current;
    private int _closed;

    internal RelayChannelStream(IRelayConnection relay, string channelId, int bufferedFrames = 64)
    {
        _relay = relay;
        ChannelId = channelId;
        _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(bufferedFrames) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public string ChannelId { get; }
    public string? CloseReason { get; private set; }

    internal async Task DeliverAsync(byte[] frame)
    {
        try { await _incoming.Writer.WriteAsync(frame); }
        catch (ChannelClosedException) { }
    }

    /// <summary>The relay closed the channel. Readers see end of stream; writers fail.</summary>
    internal void Complete(string reason)
    {
        CloseReason ??= reason;
        Interlocked.Exchange(ref _closed, 1);
        _incoming.Writer.TryComplete();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(ct)) return 0;
            if (_incoming.Reader.TryRead(out var frame)) _current = frame;
        }
        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        while (!data.IsEmpty)
        {
            if (Volatile.Read(ref _closed) == 1) throw new IOException($"The relay channel closed ({CloseReason ?? "closed"}).");
            var count = Math.Min(data.Length, RelayProtocol.MaxFrameBytes);
            await _relay.SendFrameAsync(ChannelId, data[..count].ToArray(), ct);
            data = data[count..];
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            CloseReason ??= "closed";
            _incoming.Writer.TryComplete();
            _ = _relay.CloseChannelAsync(ChannelId).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }
}
