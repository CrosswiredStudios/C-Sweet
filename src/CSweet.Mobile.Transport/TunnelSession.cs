using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.Mobile.Transport;

/// <summary>A complete response carried over the tunnel: status, content type, optional content encoding and body bytes.</summary>
public sealed record TunnelReply(int Status, string? ContentType, string? ContentEncoding, byte[] Body)
{
    public static TunnelReply Empty(int status) => new(status, null, null, []);
}

/// <summary>
/// After hello/welcome, an enrolled session carries many requests at once. Each request has an id; each response is a
/// head line followed by chunk lines (base64, at most 48 KiB raw each). Lines are newline-delimited JSON. This is the
/// prototype framing that TS-01 replaces with a binary codec.
/// </summary>
internal static class TunnelFrames
{
    public const int ChunkBytes = 48 * 1024;
    public const int MaxLine = 128 * 1024;

    public sealed record Head(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("contentEncoding")] string? ContentEncoding,
        [property: JsonPropertyName("length")] long Length);

    public sealed record Chunk(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("data")] string Data,
        [property: JsonPropertyName("end")] bool End);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length >= MaxLine) throw new InvalidDataException("Frame too large.");
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteReplyAsync(Stream stream, SemaphoreSlim writeLock, int id, TunnelReply reply, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await WriteAsync(stream, new Head("head", id, reply.Status, reply.ContentType, reply.ContentEncoding, reply.Body.Length), ct);
            var offset = 0;
            do
            {
                var count = Math.Min(ChunkBytes, reply.Body.Length - offset);
                await WriteAsync(stream, new Chunk("chunk", id, Convert.ToBase64String(reply.Body, offset, count), offset + count >= reply.Body.Length), ct);
                offset += count;
            } while (offset < reply.Body.Length);
        }
        finally
        {
            writeLock.Release();
        }
    }
}

/// <summary>
/// Buffered newline reader for the tunnel. Created only after hello/welcome (which are read byte by byte so nothing is
/// read past them); from then on it owns the stream's read side.
/// </summary>
internal sealed class TunnelLineReader(Stream stream)
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>Reads one line, or null at a clean end of stream.</summary>
    public async Task<JsonElement?> ReadAsync(CancellationToken ct)
    {
        var scanned = _start;
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', scanned, _end - scanned);
            if (newline >= 0)
            {
                using var document = JsonDocument.Parse(_buffer.AsMemory(_start, newline - _start));
                _start = newline + 1;
                return document.RootElement.Clone();
            }
            scanned = _end;
            if (_end - _start >= TunnelFrames.MaxLine) throw new InvalidDataException("Frame too large.");
            if (_end == _buffer.Length)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    scanned -= _start;
                    _end -= _start;
                    _start = 0;
                }
                else
                {
                    Array.Resize(ref _buffer, Math.Min(TunnelFrames.MaxLine + 1, _buffer.Length * 2));
                }
            }
            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
            if (read == 0) return _end == _start ? null : throw new EndOfStreamException();
            _end += read;
        }
    }
}

/// <summary>Phone side of an enrolled session: sends requests concurrently and matches responses by id.</summary>
public sealed class PhoneTunnelClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _readLoop;
    private int _nextId;

    private readonly TunnelLineReader _reader;

    public PhoneTunnelClient(Stream stream)
    {
        _stream = stream;
        _reader = new TunnelLineReader(stream);
    }

    /// <summary>Set when the session ended; every later request fails.</summary>
    public string? EndedReason { get; private set; }

    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    public async Task<TunnelReply> SendAsync(string method, string path, long maxBytes, CancellationToken ct)
    {
        if (EndedReason is not null) throw new IOException($"The Headquarters session ended ({EndedReason}).");
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(maxBytes);
        _pending[id] = pending;
        try
        {
            await _writeLock.WaitAsync(ct);
            try { await TunnelFrames.WriteAsync(_stream, new MobileRequest("request", id, method, path), ct); }
            finally { _writeLock.Release(); }
            return await pending.Completion.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _reader.ReadAsync(_stopping.Token) is { } frame)
            {
                var id = frame.GetProperty("id").GetInt32();
                if (!_pending.TryGetValue(id, out var pending)) continue;
                switch (frame.GetProperty("type").GetString())
                {
                    case "head":
                        var head = frame.Deserialize<TunnelFrames.Head>()!;
                        if (head.Length > pending.MaxBytes || head.Length < 0) { pending.Completion.TrySetException(new InvalidDataException("Response too large.")); continue; }
                        pending.Head = head;
                        pending.Body = new MemoryStream((int)Math.Min(head.Length, int.MaxValue));
                        break;
                    case "chunk":
                        var chunk = frame.Deserialize<TunnelFrames.Chunk>()!;
                        if (pending.Head is null || pending.Body is null) continue;
                        var data = Convert.FromBase64String(chunk.Data);
                        if (pending.Body.Length + data.Length > pending.Head.Length) { pending.Completion.TrySetException(new InvalidDataException("Response longer than announced.")); continue; }
                        pending.Body.Write(data);
                        if (chunk.End)
                            pending.Completion.TrySetResult(new TunnelReply(pending.Head.Status, pending.Head.ContentType, pending.Head.ContentEncoding, pending.Body.ToArray()));
                        break;
                }
            }
            End("closed");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or FormatException or KeyNotFoundException)
        {
            End(error is OperationCanceledException ? "stopped" : "broken");
        }
    }

    private void End(string reason)
    {
        EndedReason ??= reason;
        foreach (var pending in _pending.Values) pending.Completion.TrySetException(new IOException($"The Headquarters session ended ({reason})."));
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        End("stopped");
        if (_readLoop is not null) try { await _readLoop; } catch { }
        _stopping.Dispose();
    }

    private sealed class Pending(long maxBytes)
    {
        public long MaxBytes { get; } = maxBytes;
        public TaskCompletionSource<TunnelReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TunnelFrames.Head? Head { get; set; }
        public MemoryStream? Body { get; set; }
    }
}

/// <summary>Headquarters side of an enrolled session: answers up to <c>concurrency</c> requests at once until the phone leaves or it is idle.</summary>
public static class HeadquartersTunnelServer
{
    public static async Task ServeAsync(Stream stream, Func<MobileRequest, CancellationToken, Task<TunnelReply>> handle,
        int concurrency, TimeSpan idle, CancellationToken ct)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        var reader = new TunnelLineReader(stream);
        var slots = new SemaphoreSlim(concurrency, concurrency);
        var running = new List<Task>();
        while (true)
        {
            JsonElement? frame;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(idle);
                try { frame = await reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // idle
            }
            if (frame is null) break;
            var request = frame.Value.Deserialize<MobileRequest>();
            if (request is null || request.Type != "request") throw new InvalidDataException("Expected request.");
            await slots.WaitAsync(ct);
            running.RemoveAll(x => x.IsCompleted);
            running.Add(Task.Run(async () =>
            {
                try
                {
                    TunnelReply reply;
                    try { reply = await handle(request, ct); }
                    catch (Exception error) when (error is not OperationCanceledException) { reply = TunnelReply.Empty(500); }
                    await TunnelFrames.WriteReplyAsync(stream, writeLock, request.Id, reply, ct);
                }
                finally
                {
                    slots.Release();
                }
            }, ct));
        }
        try { await Task.WhenAll(running); } catch { }
    }
}
