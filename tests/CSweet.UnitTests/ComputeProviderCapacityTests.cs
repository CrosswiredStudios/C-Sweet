using System.Text.Json.Nodes;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeProviderCapacityTests
{
    [Theory]
    [InlineData("count")]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("disk")]
    public async Task Failed_provision_retains_each_capacity_limit_after_restart(string dimension)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var resources = f.Packet.Specification.Resources;
        var capacity = new ComputeProviderCapacity(dimension == "count" ? 1 : 2, new(
            resources.CpuCount * (dimension == "cpu" ? 1 : 2),
            resources.MemoryMiB * (dimension == "memory" ? 1 : 2),
            resources.DiskMiB * (dimension == "disk" ? 1 : 2)));
        await Assert.ThrowsAsync<IOException>(() => f.Journal(capacity).RunAsync<int>(f.Verifier.Verify(f.Packet),
            (_, _) => throw new IOException("Unknown physical outcome."), default));
        var held = Assert.Single(await f.Journal(capacity).ListReservationsAsync(null, 100, default));
        Assert.Equal(resources, held.Resources);
        Assert.Equal(f.Core.Signing.Claims[0].EnvironmentLeaseExpiresAt, held.LeaseExpiresAt);
        var second = f.Core.Signing.Claims[0] with
        { EnvironmentId = Guid.NewGuid(), OperationId = Guid.NewGuid(), DispatchId = Guid.NewGuid() };
        var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(second, default) };
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal(capacity).RunAsync(f.Verifier.Verify(packet),
            (decision, _) => { calls++; return Task.FromResult(decision); }, default));
        Assert.Equal(0, calls);
        Assert.Equal(held, Assert.Single(await f.Journal(capacity).ListReservationsAsync(null, 100, default)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_stop_keeps_disk_and_identity_while_another_VM_uses_the_running_budget(bool upgradeStoppedJournal)
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var original = f.Core.Signing.Claims[0];
        var resources = f.Packet.Specification.Resources;
        var capacity = new ComputeProviderCapacity(1, resources with { DiskMiB = resources.DiskMiB * 2 });
        async Task Apply(Guid id, long generation, string action, string vm, ComputeLifecycleState observed)
        {
            var create = action == InfrastructureActions.Provision;
            var claim = original with { EnvironmentId = id, Generation = generation, OperationId = Guid.NewGuid(),
                DispatchId = Guid.NewGuid(), Action = action, ResourceId = create ? null : vm,
                TemplateDigest = create ? original.TemplateDigest : null,
                Grants = create ? original.Grants : [new(Guid.NewGuid(), 1, action, original.EnvironmentLeaseExpiresAt)] };
            var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(claim, default), Template = create ? f.Packet.Template : null };
            await f.Journal(capacity).RunPhysicalAsync(f.Verifier.Verify(packet), (decision, known, _) =>
            {
                Assert.Equal(ComputeJournalDecision.Execute, decision);
                Assert.Equal(create ? null : vm, known);
                return Task.FromResult(new ComputePhysicalOutcome<bool>(true, vm, new(observed)));
            }, default);
        }
        var first = original.EnvironmentId;
        var second = Guid.NewGuid();
        await Apply(first, 1, InfrastructureActions.Provision, "vm-a", ComputeLifecycleState.Ready);
        await Apply(first, 2, InfrastructureActions.Stop, "vm-a", ComputeLifecycleState.Stopped);
        if (upgradeStoppedJournal)
        {
            var path = Path.Combine(f.Root, "journal.json");
            var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var state = JsonNode.Parse(envelope["stateJson"]!.GetValue<string>())!;
            state["environments"]![first.ToString()]!.AsObject().Remove("stopped");
            state["resultOutbox"] = new JsonObject();
            var legacy = state.ToJsonString();
            envelope["stateJson"] = legacy;
            envelope["digest"] = ComputeProtocol.Digest(legacy);
            await File.WriteAllTextAsync(path, envelope.ToJsonString());
            await f.Journal(capacity).InitializeAsync((_, _, _) => Task.FromResult(false), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(second, 1, InfrastructureActions.Provision, "vm-b", ComputeLifecycleState.Ready));
            await f.Journal(capacity).InitializeAsync((id, resource, _) => Task.FromResult(id == first && resource == "vm-a"), default);
        }
        await Apply(second, 1, InfrastructureActions.Provision, "vm-b", ComputeLifecycleState.Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(first, 3, InfrastructureActions.Start, "vm-a", ComputeLifecycleState.Ready));
        await Apply(second, 2, InfrastructureActions.Stop, "vm-b", ComputeLifecycleState.Stopped);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(Guid.NewGuid(), 1, InfrastructureActions.Provision, "vm-c", ComputeLifecycleState.Ready));
        await Apply(first, 3, InfrastructureActions.Start, "vm-a", ComputeLifecycleState.Ready);
        Assert.Equal(2, (await f.Journal(capacity).ListReservationsAsync(null, 100, default)).Count);
    }

    [Fact]
    public async Task Concurrent_environment_claims_cannot_overbook_one_node()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var capacity = new ComputeProviderCapacity(1, f.Packet.Specification.Resources);
        var claim = f.Core.Signing.Claims[0] with
        { EnvironmentId = Guid.NewGuid(), OperationId = Guid.NewGuid(), DispatchId = Guid.NewGuid() };
        var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(claim, default) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Journal(capacity).RunAsync(f.Verifier.Verify(f.Packet), async (decision, token) =>
        { entered.SetResult(); await release.Task.WaitAsync(token); return decision; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Journal(capacity).RunAsync(f.Verifier.Verify(packet), (decision, _) => Task.FromResult(decision), default);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Equal(ComputeJournalDecision.Execute, await first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Single(await f.Journal(capacity).ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Duplicate_and_unconfirmed_destroy_retain_capacity_and_recreation_is_fenced()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var original = f.Core.Signing.Claims[0];
        await f.Journal().RunAsync(f.Verifier.Verify(f.Packet), (decision, _) => Task.FromResult(decision), default);
        Assert.Equal(ComputeJournalDecision.Observe, await f.Journal().RunAsync(f.Verifier.Verify(f.Packet),
            (decision, _) => Task.FromResult(decision), default));
        var recreation = original with { DispatchId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Generation = 2 };
        var retry = f.Packet with { Authorization = await f.Core.Signing.SignAsync(recreation, default) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Journal().RunAsync(f.Verifier.Verify(retry),
            (decision, _) => Task.FromResult(decision), default));
        var destroy = recreation with { Action = InfrastructureActions.Destroy, TemplateDigest = null,
            Grants = [new(Guid.NewGuid(), 1, InfrastructureActions.Destroy, original.ExpiresAt)] };
        var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(destroy, default), Template = null };
        await f.Journal().RunAsync(f.Verifier.Verify(packet), (decision, _) => Task.FromResult(decision), default);
        var held = Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default));
        Assert.True(held.DestroyRequested);
        Assert.Equal(2, held.Generation);
        Assert.Equal(f.Packet.Specification.Resources, held.Resources);
        Assert.Empty(await f.Journal().ListReservationsAsync(held.EnvironmentId, 100, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Journal().ListReservationsAsync(null, 101, default));
    }
}
