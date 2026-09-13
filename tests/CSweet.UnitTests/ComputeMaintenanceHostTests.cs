using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_delivery_certificate_leaves_host_and_local_lease_recovery_running(bool enableIntake)
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true);
        var executor = f.Executor();
        await executor.ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var deliveryRetrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryRetrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intakeRetrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var running = HyperVMaintenanceHost.RunAsync(Configuration(f), executor, f.Journal.Core.Time, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            throw new InvalidOperationException("Missing enrolled certificate.");
        }, code =>
        {
            if (code == "maintenance-delivery-retrying") deliveryRetrying.TrySetResult();
            if (code == "observation-recovery-retrying") recoveryRetrying.TrySetResult();
            if (code == "dispatch-intake-retrying") intakeRetrying.TrySetResult();
            if (code == "lease-maintenance-failed") leaseChecked.TrySetResult();
        }, cancellation.Token, enableIntake);
        try
        {
            await Task.WhenAll(deliveryRetrying.Task, recoveryRetrying.Task).WaitAsync(cancellation.Token);
            if (enableIntake) await intakeRetrying.Task.WaitAsync(cancellation.Token);
            Assert.False(running.IsCompleted); Assert.True(Volatile.Read(ref opens) >= (enableIntake ? 3 : 2));
            Assert.Single(f.Runner.Actions.Skip(1));
            f.Runner.FailObservation = true;
            // A fresh signed observation reaches protected execution and wakes the monitor even when inventory fails.
            var claim = f.Claim with
            {
                IssuedAt = f.Journal.Core.Time.Now, ExpiresAt = f.Journal.Core.Time.Now.AddMinutes(1),
                Mode = CSweet.Compute.Contracts.ComputeDispatchMode.Observe, Grants = [], TemplateDigest = null
            };
            var observation = f.Packet with { Authorization = await f.Journal.Core.Signing.SignAsync(claim, default), Template = null };
            await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(observation, cancellation.Token));
            await leaseChecked.Task.WaitAsync(cancellation.Token);
            Assert.True(Volatile.Read(ref opens) >= 2);
            Assert.Equal(2, (await f.Journal.Journal().ListMaintenanceEventsAsync(100, cancellation.Token)).Count);
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
    }

    [Fact]
    public async Task Corrupt_history_fails_startup_without_resetting_journal_or_opening_credentials()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var path = Path.Combine(f.Journal.Root, "journal.json");
        await File.WriteAllTextAsync(path, "corrupt");
        var opens = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => HyperVMaintenanceHost.RunAsync(Configuration(f),
            f.Executor(), f.Journal.Core.Time, (_, _) => { opens++; throw new InvalidOperationException(); }, _ => { }, default));
        Assert.Equal(0, opens); Assert.Empty(f.Runner.Scripts);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Diagnostic_failure_still_stops_workers_before_host_returns()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var executor = f.Executor(); await executor.ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => HyperVMaintenanceHost.RunAsync(Configuration(f), executor,
            f.Journal.Core.Time, (_, _) => throw new InvalidOperationException("Unavailable credential."),
            _ => throw new IOException("Diagnostic destination closed."), timeout.Token));
        var before = await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json"));
        // The host joined its monitor, so another protected read can complete immediately.
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json")));
    }

    private static ComputeProviderConfiguration Configuration(Fixture f) => new(1, f.Journal.Enrollment,
        f.Journal.Enrollment.ControlPlaneKey, "https://core.example.test", f.Journal.Root, f.Workloads,
        new(10, new(40, 81920, 409600)), new string('A', 40), StoreLocation.LocalMachine);
}
