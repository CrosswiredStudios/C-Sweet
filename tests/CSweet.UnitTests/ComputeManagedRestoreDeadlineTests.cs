using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeManagedRestoreDeadlineTests
{
    [Theory]
    [InlineData("managed", 420)]
    [InlineData("grant", 180)]
    [InlineData("lease", 240)]
    [InlineData("plain", 60)]
    [InlineData("shutdown", 420)]
    [InlineData("late-admission", 420)]
    public async Task Verified_managed_work_survives_admission_expiry_but_not_its_signed_bounds_and_never_reexecutes(string scenario, int seconds)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
            Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Ready))), default);
        var clock = new ManualClock(f.Core.Time.GetUtcNow());
        var original = f.Core.Signing.Claims[0];
        var managed = scenario != "plain";
        var workload = new ComputeWorkload(new(Guid.NewGuid(), "/bin/echo", "/tmp", ["bounded-final-command"], 30, 8192),
            NodeDependencies: managed ? ComputeNodeDependencyTests.Dependencies() : null);
        var grantExpiry = clock.GetUtcNow().AddMinutes(scenario == "grant" ? 3 : 30);
        var claim = original with {
            DispatchId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Generation = 2, Action = InfrastructureActions.Execute,
            Mode = ComputeDispatchMode.Execute, ResourceId = "vm-1", TemplateDigest = null,
            IssuedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddMinutes(1),
            EnvironmentLeaseExpiresAt = clock.GetUtcNow().AddMinutes(scenario == "lease" ? 4 : 10),
            Grants = workload.RequiredActions.Select(action => new ComputeActionAuthorization(Guid.NewGuid(), 1, action, grantExpiry)).ToArray(),
            WorkloadDigest = ComputeProtocol.Digest(JsonSerializer.Serialize(workload, ComputeProtocol.Json))
        };
        var packet = new ComputeDispatchPacket(await f.Core.Signing.SignAsync(claim, default), f.Packet.Specification, null, workload);
        var verifier = new ComputeDispatchVerifier(f.Enrollment,
            new(claim.ProviderId, [], [InfrastructureActions.Execute], [ComputeNetworkMode.None], false, true),
            new(4, 8192, 40960), new Dictionary<string, ComputeTemplate>(), clock);
        var journal = f.Journal(clock: clock); var effects = 0;
        using var shutdown = new CancellationTokenSource();
        var verified = verifier.Verify(packet);
        if (scenario == "late-admission")
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => journal.RunAsync(verified, (_, _) => { effects++; return Task.FromResult(true); }, default));
            Assert.Equal(0, effects); return;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.RunPhysicalAsync<bool>(verified, (_, _, token) => {
            effects++;
            clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.False(token.IsCancellationRequested);
            if (managed) Assert.True(clock.GetUtcNow() > claim.ExpiresAt);
            if (scenario == "shutdown") shutdown.Cancel(); else clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(token.IsCancellationRequested); token.ThrowIfCancellationRequested();
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, null));
        }, shutdown.Token));
        Assert.Equal(1, effects);
        var observe = claim with { DispatchId = Guid.NewGuid(), Mode = ComputeDispatchMode.Observe,
            IssuedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddMinutes(1), Grants = [], WorkloadDigest = null };
        var observed = verifier.Verify(new(await f.Core.Signing.SignAsync(observe, default), packet.Specification, null));
        Assert.Equal(ComputeJournalDecision.Observe, await journal.RunAsync(observed, (decision, _) => Task.FromResult(decision), default));
        Assert.Equal(1, effects);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        private readonly List<ClockTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            current += elapsed;
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset due = DateTimeOffset.MaxValue;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (disposed) return false; due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.current + dueTime; return true; }
            public void Fire() { if (!disposed && due <= clock.current) { due = DateTimeOffset.MaxValue; callback(state); } }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
