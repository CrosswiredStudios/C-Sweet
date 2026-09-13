namespace CSweet.Compute.Contracts;

public static class ComputeStreamRelay
{
    public static async Task RunAsync(Stream first, Stream second, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var interrupt = stop.Token.Register(() => { first.Dispose(); second.Dispose(); });
        var outgoing = CopyAsync(first, second, stop.Token);
        var incoming = CopyAsync(second, first, stop.Token);
        try { await await Task.WhenAny(outgoing, incoming); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(outgoing, incoming); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[8192]; long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            if (total > 8 * 1024 * 1024) throw new IOException("Forwarded connection exceeded its byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            await destination.FlushAsync(token);
        }
    }
}
