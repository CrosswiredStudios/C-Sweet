using CSweet.Compute.Contracts;

namespace CSweet.Compute.Guest;

/// <summary>
/// Implemented only by an installed guest runtime. Creation has no process side effects. Start must
/// contain the process and all descendants before allowing guest code to execute, without inherited secrets.
/// Stop must confirm all contained processes are gone and release native resources, even after failed Start.
/// Output streams exist before Start and remain readable until the coordinator disposes them. No host fallback is permitted.
/// </summary>
public interface IComputeGuestProcess : IDisposable
{
    void IDisposable.Dispose() { }
    Task StartAsync(CancellationToken token);
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    Task<int> WaitForExitAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
}

public sealed record ComputeGuestExecutionResult(Guid RequestId, int? ExitCode, bool TimedOut,
    byte[] StandardOutput, byte[] StandardError, bool Truncated);

/// <summary>Guest-only bounded execution coordinator. The injected adapter supplies OS containment.</summary>
public sealed class ComputeGuestExecution(string operatingSystem, Func<ComputeGuestCommand, IComputeGuestProcess> create)
{
    private int busy;
    private int unhealthy;
    public bool AcceptingWork => Volatile.Read(ref busy) == 0 && Volatile.Read(ref unhealthy) == 0;

    public async Task<ComputeGuestExecutionResult> ExecuteAsync(ComputeGuestCommand request, CancellationToken token)
    {
        var command = request.ValidateAndSnapshot(operatingSystem);
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("Guest execution is busy.");
        try
        {
            if (Volatile.Read(ref unhealthy) != 0) throw new InvalidOperationException("Guest execution requires recovery.");
            var process = create(command) ?? throw new InvalidOperationException("Guest process adapter is missing.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
            var gate = new object(); var remaining = command.MaximumOutputBytes; var truncated = false;
            Task? stop = null; Task? output = null; Task? error = null;
            int? exitCode = null; var timedOut = false;
            async Task StopAsync()
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.StopAsync(cleanup.Token).WaitAsync(cleanup.Token); }
                catch
                {
                    Interlocked.Exchange(ref unhealthy, 1);
                    throw new IOException("Guest process cleanup failed; execution requires recovery.");
                }
            }
            async Task DrainAsync(Stream source, MemoryStream destination)
            {
                var buffer = new byte[4096];
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var count = await source.ReadAsync(buffer, linked.Token);
                    if (count == 0) return;
                    lock (gate)
                    {
                        var take = Math.Min(count, remaining);
                        destination.Write(buffer, 0, take); remaining -= take;
                        truncated |= take != count;
                    }
                }
            }
            try
            {
                await process.StartAsync(linked.Token);
                output = DrainAsync(process.StandardOutput, stdout);
                error = DrainAsync(process.StandardError, stderr);
                exitCode = await process.WaitForExitAsync(linked.Token);
                // A parent may exit while descendants still hold output handles or keep doing work.
                await (stop = StopAsync());
                await Task.WhenAll(output, error).WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                timedOut = true; exitCode = null;
            }
            finally
            {
                try { await (stop ??= StopAsync()); }
                finally
                {
                    linked.Cancel();
                    // Always observe drains and attempt both disposals, including when an adapter fails.
                    Exception? cleanupError = null;
                    try { process.StandardOutput.Dispose(); } catch (Exception fault) { cleanupError = fault; }
                    try { process.StandardError.Dispose(); } catch (Exception fault) { cleanupError ??= fault; }
                    try
                    {
                        await Task.WhenAll(output ?? Task.CompletedTask, error ?? Task.CompletedTask)
                            .WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception fault) { cleanupError ??= fault; }
                    try { process.Dispose(); } catch (Exception fault) { cleanupError ??= fault; }
                    if (cleanupError is not null)
                    {
                        Interlocked.Exchange(ref unhealthy, 1);
                        throw new IOException("Guest output cleanup failed; execution requires recovery.");
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return new(command.RequestId, exitCode, timedOut, stdout.ToArray(), stderr.ToArray(), truncated);
        }
        finally { Volatile.Write(ref busy, 0); }
    }
}
