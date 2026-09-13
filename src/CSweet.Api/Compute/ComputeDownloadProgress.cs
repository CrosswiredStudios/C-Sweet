using System.Text.RegularExpressions;
using CSweet.Contracts.Compute;

namespace CSweet.Api.Compute;

/// <summary>Observes download throughput; estimates never imply that the VM will be ready at that time.</summary>
public sealed partial class ComputeDownloadProgress(IHttpClientFactory clients, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (long? Size, DateTimeOffset Expires)> sizes = [];
    private readonly Dictionary<Guid, Samples> samples = [];
    private sealed class Samples(DateTimeOffset attempt, string file)
    {
        public DateTimeOffset Attempt { get; } = attempt;
        public string File { get; } = file;
        public List<(DateTimeOffset At, long Bytes)> Points { get; } = [];
        public DateTimeOffset LastGrowth { get; set; }
    }

    [GeneratedRegex(@"^ubuntu-(\d{2}\.\d{2}(?:\.\d+)?)-live-server-amd64\.iso$")]
    private static partial Regex UbuntuImage();

    public async Task<ComputeSetupStatus> EnrichAsync(Guid id, DateTimeOffset attempt, string? file,
        ComputeSetupStatus status, CancellationToken token)
    {
        if (file is null || status.State != "Running" || status.DownloadedBytes is null)
        {
            lock (gate) samples.Remove(id);
            return status;
        }
        var total = await TotalAsync(file, token);
        return Observe(id, attempt, file, status, total, clock.GetUtcNow());
    }

    internal ComputeSetupStatus Observe(Guid id, DateTimeOffset attempt, string file, ComputeSetupStatus status,
        long? total, DateTimeOffset now)
    {
        if (status.DownloadedBytes is not { } bytes || bytes < 0) return status;
        lock (gate)
        {
            if (!samples.TryGetValue(id, out var series) || series.Attempt != attempt || series.File != file ||
                series.Points.Count > 0 && bytes < series.Points[^1].Bytes)
            {
                if (samples.Count >= 128) samples.Remove(samples.First().Key);
                samples[id] = series = new(attempt, file) { LastGrowth = now };
            }
            if (series.Points.Count > 0 && bytes > series.Points[^1].Bytes) series.LastGrowth = now;
            // Multiple viewers must not overweight a single moment or grow storage without bound.
            if (series.Points.Count == 0 || now - series.Points[^1].At >= TimeSpan.FromSeconds(1)) series.Points.Add((now, bytes));
            while (series.Points.Count > 2 && now - series.Points[0].At > TimeSpan.FromMinutes(2)) series.Points.RemoveAt(0);
            var first = series.Points[0];
            var elapsed = (now - first.At).TotalSeconds;
            double? rate = elapsed >= 5 && now - series.LastGrowth < TimeSpan.FromSeconds(30) && bytes > first.Bytes
                ? (bytes - first.Bytes) / elapsed : null;
            total = total > 0 && total >= bytes ? total : null;
            var complete = total is not null && bytes == total;
            return status with { TotalDownloadBytes = total, DownloadBytesPerSecond = complete ? null : rate,
                DownloadWaitingForProgress = !complete && now - series.LastGrowth >= TimeSpan.FromSeconds(30),
                DownloadSecondsRemaining = complete ? 0 : total is not null && rate > 0 ? (total.Value - bytes) / rate : null };
        }
    }

    private async Task<long?> TotalAsync(string file, CancellationToken token)
    {
        var match = UbuntuImage().Match(file);
        if (!match.Success) return null;
        var now = clock.GetUtcNow();
        lock (gate) if (sizes.TryGetValue(file, out var cached) && cached.Expires > now) return cached.Size;
        long? size = null;
        try
        {
            // The local filename is constrained above; no supplied URLs or arbitrary hosts are accepted.
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://releases.ubuntu.com/{match.Groups[1].Value}/{file}");
            using var response = await clients.CreateClient("ComputeDownloadMetadata").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0) size = response.Content.Headers.ContentLength;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        lock (gate)
        {
            if (sizes.Count >= 32) sizes.Remove(sizes.First().Key);
            sizes[file] = (size, now.Add(size is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(30)));
        }
        return size;
    }
}
