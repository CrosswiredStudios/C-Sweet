using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeReplayJournalTests
{
    internal sealed class Fixture : IAsyncDisposable
    {
        public ComputeDispatchTests.Fixture Core { get; } = new();
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-compute-journal-" + Guid.NewGuid().ToString("N")));
        public ComputeProviderEnrollment Enrollment { get; private set; } = null!;
        public ComputeDispatchVerifier Verifier { get; private set; } = null!;
        public ComputeDispatchPacket Packet { get; private set; } = null!;
        public ComputeReplayJournal Journal(ComputeProviderCapacity? capacity = null) => new(Root, Enrollment, capacity ?? new(10, new(40, 81920, 409600)), Core.Time, path =>
        {
            var resolved = Path.GetFullPath(path);
            if (resolved != Root && !resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Test journal path escaped its temporary directory.");
            if (Path.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Test journal path is a link.");
        });
        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(Root);
            await Core.InitializeAsync(); Packet = (await Core.Authorizer().ClaimAsync(Core.OperationId, default))!;
            var claim = Core.Signing.Claims[0];
            Enrollment = new(claim.OrganizationId, claim.NodeId, claim.ProviderId, await Core.Signing.GetIdentityAsync(default));
            Verifier = new(Enrollment, new(claim.ProviderId, ["ubuntu-clean"], [InfrastructureActions.Provision, InfrastructureActions.Destroy, InfrastructureActions.Start],
                [ComputeNetworkMode.None], false, false), new(4, 8192, 40960),
                new Dictionary<string, ComputeTemplate> { [Packet.Template!.Id] = Packet.Template }, Core.Time);
            await Journal().InitializeAsync(default);
        }
        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            if (Directory.Exists(Root) && Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(Root).StartsWith("csweet-compute-journal-", StringComparison.Ordinal)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public async Task Failure_after_claim_never_reexecutes_after_restart()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var verified = f.Verifier.Verify(f.Packet);
        await Assert.ThrowsAsync<IOException>(() => f.Journal().RunAsync<int>(verified, (decision, _) =>
        {
            Assert.Equal(ComputeJournalDecision.Execute, decision);
            throw new IOException("Lost provider response after possible effect.");
        }, default));
        Assert.Equal(ComputeJournalDecision.Observe, await f.Journal().RunAsync(verified, (decision, _) => Task.FromResult(decision), default));
    }

    [Fact]
    public async Task Separate_journal_instances_serialize_the_entire_effect_and_duplicate_observes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var verified = f.Verifier.Verify(f.Packet);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Journal().RunAsync(verified, async (decision, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token); return decision;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Journal().RunAsync(verified, (decision, _) => Task.FromResult(decision), default);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Equal(ComputeJournalDecision.Execute, await first);
        Assert.Equal(ComputeJournalDecision.Observe, await second);
    }

    [Fact]
    public async Task Background_read_waits_through_long_effect_and_remains_cancellable()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = f.Journal().RunAsync(f.Verifier.Verify(f.Packet), async (decision, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token); return decision;
        }, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var read = f.Journal().ListReservationsAsync(null, 100, timeout.Token);
        try
        {
            // Exceeds the former five-second lock timeout which killed the service.
            await Task.Delay(TimeSpan.FromSeconds(5.5), timeout.Token);
            Assert.False(read.IsCompleted);
            using var cancelled = new CancellationTokenSource();
            var cancelledRead = f.Journal().ListReservationsAsync(null, 100, cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);
        }
        finally
        {
            release.TrySetResult();
            await effect;
            await read;
        }
        Assert.Single(await read);
    }

    [Fact]
    public async Task Destroy_fences_old_generation_and_prevents_later_resurrection()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var initial = f.Core.Signing.Claims[0];
        var destroy = initial with { DispatchId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Generation = 2,
            Action = InfrastructureActions.Destroy, TemplateDigest = null,
            Grants = [new(Guid.NewGuid(), 1, InfrastructureActions.Destroy, initial.ExpiresAt)] };
        var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(destroy, default), Template = null };
        await f.Journal().RunAsync(f.Verifier.Verify(packet), (decision, _) => Task.FromResult(decision), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Journal().RunAsync(f.Verifier.Verify(f.Packet),
            (decision, _) => Task.FromResult(decision), default));
        var start = destroy with { DispatchId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Generation = 3,
            Action = InfrastructureActions.Start, ResourceId = "vm-1", Grants = [new(Guid.NewGuid(), 1, InfrastructureActions.Start, initial.ExpiresAt)] };
        packet = packet with { Authorization = await f.Core.Signing.SignAsync(start, default) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Journal().RunAsync(f.Verifier.Verify(packet),
            (decision, _) => Task.FromResult(decision), default));
    }

    [Fact]
    public async Task Corrupt_or_missing_history_is_never_silently_initialized()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "journal.json"), "{\"unexpected\":true}");
        await Assert.ThrowsAsync<JsonException>(() => f.Journal().RunAsync(f.Verifier.Verify(f.Packet), (decision, _) => Task.FromResult(decision), default));
        File.Delete(Path.Combine(f.Root, "journal.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().InitializeAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().RunAsync(f.Verifier.Verify(f.Packet), (decision, _) => Task.FromResult(decision), default));
    }

    [Fact]
    public async Task Only_empty_history_can_rebind_organization_while_node_identity_remains_fixed()
    {
        await using (var empty = new Fixture())
        {
            await empty.InitializeAsync();
            var replacement = empty.Enrollment with { OrganizationId = Guid.NewGuid() };
            await empty.Journal().RebindEmptyEnrollmentAsync(replacement, default);
            await new ComputeReplayJournal(empty.Root, replacement, new(10, new(40, 81920, 409600)),
                empty.Core.Time, _ => { }).InitializeAsync(default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new ComputeReplayJournal(empty.Root, replacement, new(10, new(40, 81920, 409600)),
                    empty.Core.Time, _ => { }).RebindEmptyEnrollmentAsync(
                        replacement with { NodeId = Guid.NewGuid() }, default));
        }

        await using var used = new Fixture();
        await used.InitializeAsync();
        await used.Journal().RunAsync(used.Verifier.Verify(used.Packet), (_, _) => Task.FromResult(true), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => used.Journal().RebindEmptyEnrollmentAsync(
            used.Enrollment with { OrganizationId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task Recovery_retires_ephemeral_history_archives_it_and_starts_an_empty_enrollment()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var verified = f.Verifier.Verify(f.Packet);
        await f.Journal().RunPhysicalAsync(verified, (_, _, _) =>
            Task.FromResult(new ComputePhysicalOutcome<bool>(true, "3f25839d-3834-44cc-98b4-b80b43da33c1")), default);
        var replacement = f.Enrollment with { OrganizationId = Guid.NewGuid() };
        var retired = new List<ComputePhysicalReservation>();

        await f.Journal().RebindRetiredEnrollmentAsync(replacement, (reservation, _) =>
        {
            retired.Add(reservation);
            return Task.FromResult(true);
        }, default);

        Assert.Single(retired);
        Assert.Single(Directory.GetFiles(f.Root, $"retired-{f.Enrollment.OrganizationId:N}.json"));
        var rebound = new ComputeReplayJournal(f.Root, replacement, new(10, new(40, 81920, 409600)),
            f.Core.Time, _ => { });
        Assert.Empty(await rebound.ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Recovery_does_not_rebind_when_physical_retirement_is_unconfirmed()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Journal().RunAsync(f.Verifier.Verify(f.Packet), (_, _) => Task.FromResult(true), default);
        var replacement = f.Enrollment with { OrganizationId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal().RebindRetiredEnrollmentAsync(
            replacement, (_, _) => Task.FromResult(false), default));

        Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default));
        Assert.Empty(Directory.GetFiles(f.Root, "retired-*.json"));
    }

    [Fact]
    public void Windows_acl_guard_rejects_unprivileged_writers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        WindowsComputeProtectedPaths.VerifyRules(security);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsComputeProtectedPaths.VerifyRules(security));
    }
}
