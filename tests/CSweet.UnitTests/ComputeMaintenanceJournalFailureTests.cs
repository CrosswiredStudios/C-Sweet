using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceJournalFailureTests
{
    [Fact]
    public async Task Failed_post_effect_persistence_retains_request_without_inventing_a_physical_failure()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        var failNextWrite = false;
        var journal = new ComputeReplayJournal(f.Journal.Root, f.Journal.Enrollment,
            new(10, new(40, 81920, 409600)), f.Journal.Core.Time, _ =>
            {
                if (failNextWrite) { failNextWrite = false; throw new IOException("Injected post-effect journal failure."); }
            });
        await Assert.ThrowsAsync<IOException>(() => journal.EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, (authority, _) =>
        {
            failNextWrite = true;
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, authority.ResourceId));
        }, default));
        var entry = Assert.Single(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        var evidence = JsonSerializer.Deserialize<ComputeMaintenanceEvent>(entry.EventJson, ComputeProtocol.Json)!;
        Assert.Equal(ComputeMaintenancePhase.Requested, evidence.Phase);
        Assert.True(Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).LeaseExpired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Earlier_journal_formats_cannot_invent_missing_provisioning_attribution(int version)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var path = Path.Combine(f.Journal.Root, "journal.json");
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        envelope["version"] = version;
        var content = envelope.ToJsonString(); await File.WriteAllTextAsync(path, content);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal.Journal().InitializeAsync(default));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }
}
