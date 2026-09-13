using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeResultOutboxTests
{
    [Fact]
    public async Task Restart_preserves_identity_sequence_and_evidence_and_late_acknowledgement_cannot_remove_new_observation()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await Observe(f);
        var first = Assert.Single(await f.Journal().ListResultsAsync(100, default));
        Assert.Equal(1, first.Result.Sequence); Assert.Equal("vm-1", first.Result.ResourceId);
        Assert.Equal(first.Digest, ComputeProtocol.Digest(JsonSerializer.Serialize(first.Result, ComputeProtocol.Json)));
        Assert.Equal(first, Assert.Single(await f.Journal().ListResultsAsync(100, default)));
        await Observe(f);
        var second = Assert.Single(await f.Journal().ListResultsAsync(100, default));
        Assert.Equal(2, second.Result.Sequence);
        Assert.False(await f.Journal().AcknowledgeResultAsync(Ack(first), default));
        Assert.Equal(second, Assert.Single(await f.Journal().ListResultsAsync(100, default)));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().AcknowledgeResultAsync(Ack(second) with { PayloadDigest = "wrong" }, default));
        Assert.True(await f.Journal().AcknowledgeResultAsync(Ack(second), default));
        Assert.Empty(await f.Journal().ListResultsAsync(100, default));
        Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default));
        await Observe(f);
        Assert.Equal(3, Assert.Single(await f.Journal().ListResultsAsync(100, default)).Result.Sequence);
    }

    [Fact]
    public async Task Cancelled_caller_after_effect_does_not_discard_completed_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning)));
        }, cancellation.Token);
        Assert.Equal("vm-1", Assert.Single(await f.Journal().ListResultsAsync(100, default)).Result.ResourceId);
    }

    [Fact]
    public async Task Concurrent_journals_allocate_distinct_sequences_with_only_one_execution_claim()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var executions = 0;
        async Task Run() => await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (decision, _, _) =>
        {
            if (decision == ComputeJournalDecision.Execute) Interlocked.Increment(ref executions);
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning)));
        }, default);
        await Task.WhenAll(Run(), Run());
        Assert.Equal(1, executions);
        Assert.Equal(2, Assert.Single(await f.Journal().ListResultsAsync(100, default)).Result.Sequence);
    }

    [Fact]
    public async Task Invalid_physical_claim_leaves_execution_fence_and_no_fabricated_result()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
            Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Destroyed, true))), default));
        Assert.Empty(await f.Journal().ListResultsAsync(100, default));
        Assert.Equal(ComputeJournalDecision.Observe, await f.Journal().RunAsync(f.Verifier.Verify(f.Packet), (decision, _) => Task.FromResult(decision), default));
    }

    [Fact]
    public async Task HyperV_executor_records_bootstrapping_without_claiming_guest_readiness()
    {
        await using var f = new ComputeHyperVExecutorTests.Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        Assert.Equal(ComputeLifecycleState.Bootstrapping, row.Result.State); Assert.False(row.Result.TeardownConfirmed);
        await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.Equal(2, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)).Result.Sequence);
    }

    [Fact]
    public async Task Failed_post_effect_commit_retains_fence_and_recovery_reobserves_before_queuing()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var failWrite = false;
        var journal = new ComputeReplayJournal(f.Root, f.Enrollment, new(10, new(40, 81920, 409600)), f.Core.Time,
            path => { if (failWrite && Path.GetFileName(path) == "journal.json") throw new IOException("Injected persistence failure."); });
        await Assert.ThrowsAsync<IOException>(() => journal.RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
        {
            failWrite = true;
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning)));
        }, default));
        Assert.Empty(await f.Journal().ListResultsAsync(100, default));
        Assert.Null(Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
        await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (decision, _, _) =>
        {
            Assert.Equal(ComputeJournalDecision.Observe, decision);
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning)));
        }, default);
        Assert.Equal(1, Assert.Single(await f.Journal().ListResultsAsync(100, default)).Result.Sequence);
    }

    [Fact]
    public async Task Prior_journal_format_is_rejected_without_resetting_history()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        var path = Path.Combine(f.Root, "journal.json");
        var json = await File.ReadAllTextAsync(path);
        using var parsed = JsonDocument.Parse(json);
        var old = JsonSerializer.Serialize(new { version = 3, stateJson = parsed.RootElement.GetProperty("stateJson").GetString(),
            digest = parsed.RootElement.GetProperty("digest").GetString() }, ComputeProtocol.Json);
        await File.WriteAllTextAsync(path, old);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().ListResultsAsync(100, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().InitializeAsync(default));
        Assert.Equal(old, await File.ReadAllTextAsync(path));
    }
    private static Task<bool> Observe(Fixture f) => f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
        Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning))), default);
    private static ComputeResultAcknowledgement Ack(ComputeResultOutboxEntry row) => new(row.Result.OperationId, row.Result.Sequence, row.Digest, false);
}
