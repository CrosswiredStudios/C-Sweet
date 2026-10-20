using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeReplayJournalFileContentionTests
{
    [Fact]
    public async Task Brief_windows_reader_does_not_lose_history_or_replay_the_effect()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Fixture(); await f.InitializeAsync();
        var effects = 0;
        var reader = HoldReplacement(f);
        try
        {
            var operation = f.Journal().RunAsync(f.Verifier.Verify(f.Packet), (_, _) =>
                Task.FromResult(Interlocked.Increment(ref effects)), default);
            await WaitForPendingCommit(f);
            await Task.Delay(150);
            Assert.Equal(0, effects);
            Assert.False(operation.IsCompleted);
            reader.Dispose();
            Assert.Equal(1, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { reader.Dispose(); }
        Assert.Equal(ComputeJournalDecision.Observe, await f.Journal().RunAsync(f.Verifier.Verify(f.Packet),
            (decision, _) => Task.FromResult(decision), default));
        Assert.Equal(1, effects);
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistent_or_cancelled_contention_preserves_prior_journal_and_never_starts_effect(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Fixture(); await f.InitializeAsync();
        var path = Path.Combine(f.Root, "journal.json");
        var original = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        using (HoldReplacement(f))
        {
            var operation = f.Journal().RunAsync<bool>(f.Verifier.Verify(f.Packet),
                (_, _) => throw new InvalidOperationException("Effect must not run before its fence is durable."), cancellation.Token);
            await WaitForPendingCommit(f);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                var error = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        Assert.Equal(ComputeJournalDecision.Execute, await f.Journal().RunAsync(f.Verifier.Verify(f.Packet),
            (decision, _) => Task.FromResult(decision), default));
    }

    private static FileStream HoldReplacement(Fixture f) => new(Path.Combine(f.Root, "journal.json"),
        FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static async Task WaitForPendingCommit(Fixture f)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.GetFiles(f.Root, "*.tmp").Length == 0) await Task.Delay(5, timeout.Token);
    }
}
