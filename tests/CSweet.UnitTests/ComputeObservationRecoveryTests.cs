using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeObservationRecoveryTests
{
    [Fact]
    public async Task Expired_evidence_requires_fresh_core_claim_and_physical_observation_before_new_sequence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor();
        await executor.ExecuteAsync(f.Packet, default);
        var old = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        f.Journal.Core.Time.Now = old.Result.ExpiresAt; f.Runner.PhysicalState = "Running";
        var pass = await new ComputeObservationRecovery(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time).RecoverAsync(null,
            (id, token) => f.Journal.Core.Authorizer().ClaimAsync(id, token),
            async (packet, token) => { await executor.ExecuteAsync(packet, token); }, default);
        Assert.Equal(1, pass.Observed); Assert.Equal(0, pass.Deferred);
        var fresh = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        Assert.Equal(2, fresh.Result.Sequence); Assert.Equal(ComputeLifecycleState.Bootstrapping, fresh.Result.State);
        Assert.Equal(f.Journal.Core.Time.Now, fresh.Result.ObservedAt); Assert.NotEqual(old.Digest, fresh.Digest);
        Assert.Empty(f.Runner.Actions.Skip(1)); Assert.Equal(1, f.PayloadOpens);
    }

    [Fact]
    public async Task Unavailable_claim_preserves_expired_evidence_and_fresh_evidence_needs_no_claim()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var executor = f.Executor();
        await executor.ExecuteAsync(f.Packet, default);
        var recovery = new ComputeObservationRecovery(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time);
        var fresh = await recovery.RecoverAsync(null, (_, _) => throw new InvalidOperationException("No claim needed."),
            (_, _) => throw new InvalidOperationException("No observation needed."), default);
        Assert.Equal(0, fresh.Observed);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        f.Journal.Core.Time.Now = row.Result.ExpiresAt;
        var expired = await recovery.RecoverAsync(null, (_, _) => Task.FromResult<ComputeDispatchPacket?>(null),
            (_, _) => throw new InvalidOperationException("No authority."), default);
        Assert.Equal(1, expired.Deferred); Assert.Equal(row, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)));
    }

    [Fact]
    public async Task Recovery_rejects_fresh_execution_authority_even_when_its_signature_is_valid()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Executor().ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)); f.Journal.Core.Time.Now = row.Result.ExpiresAt;
        var now = f.Journal.Core.Time.Now;
        var claim = f.Claim with { DispatchId = Guid.NewGuid(), IssuedAt = now, ExpiresAt = now.AddMinutes(1),
            Grants = [new(Guid.NewGuid(), 1, InfrastructureActions.Provision, now.AddMinutes(1))] };
        var packet = f.Packet with { Authorization = await f.Journal.Core.Signing.SignAsync(claim, default) };
        Assert.Equal(ComputeDispatchMode.Execute, f.Verifier.Verify(packet).Authorization.Mode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ComputeObservationRecovery(f.Journal.Journal(), f.Verifier, f.Journal.Core.Time)
            .RecoverAsync(null, (_, _) => Task.FromResult<ComputeDispatchPacket?>(packet),
                (_, _) => throw new InvalidOperationException("Execution must not run."), default));
        Assert.Equal(row, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)));
    }
}
