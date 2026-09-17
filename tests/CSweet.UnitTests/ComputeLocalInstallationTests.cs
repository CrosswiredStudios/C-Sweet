using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeLocalInstallationTests
{
    [Fact]
    public void Second_business_uses_distinct_service_and_storage_without_moving_the_first()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var legacy = ComputeLocalInstallation.Select(first, first, false);
        var added = ComputeLocalInstallation.Select(second, first, false);
        Assert.Null(legacy.BusinessId);
        Assert.Equal(ComputeWindowsService.ConfigurationPath, legacy.ConfigurationPath);
        Assert.Equal(ComputeWindowsService.ServiceName, legacy.ServiceName);
        Assert.Equal(second, added.BusinessId);
        Assert.NotEqual(legacy.ConfigurationPath, added.ConfigurationPath);
        Assert.NotEqual(legacy.ServiceName, added.ServiceName);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(legacy.Root)!, "ComputeBusinesses", second.ToString("N")), added.Root);
        Assert.Equal(legacy, ComputeLocalInstallation.Select(first, first, false));
        Assert.Equal(added, ComputeLocalInstallation.Select(second, first, true));
    }

    [Fact]
    public void Retry_keeps_business_installation_even_without_legacy_provider()
    {
        var business = Guid.NewGuid();
        Assert.Null(ComputeLocalInstallation.Select(business, null, false).BusinessId);
        Assert.Equal(business, ComputeLocalInstallation.Select(business, null, true).BusinessId);
        Assert.Throws<ArgumentException>(() => ComputeLocalInstallation.Select(Guid.Empty, null, false));
    }

    [Theory]
    [InlineData("../provider.json")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("another-service")]
    public async Task Service_selector_rejects_paths_and_invalid_business_ids(string selector)
    {
        Assert.Equal(2, await CSweet.Compute.HyperV.Program.Main(["service", selector]));
        Assert.Equal(2, await CSweet.Compute.HyperV.Program.Main(["validate-service", selector]));
    }

    [Fact]
    public async Task Two_business_journals_preserve_reservations_and_reject_cross_business_dispatch()
    {
        await using var first = new ComputeReplayJournalTests.Fixture(); await first.InitializeAsync();
        await first.Journal().RunAsync(first.Verifier.Verify(first.Packet), (decision, _) => Task.FromResult(decision), default);
        var original = File.ReadAllBytes(Path.Combine(first.Root, "journal.json"));
        await using var second = new ComputeReplayJournalTests.Fixture(); await second.InitializeAsync();
        await second.Journal().RunAsync(second.Verifier.Verify(second.Packet), (decision, _) => Task.FromResult(decision), default);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(first.Root, "journal.json")));
        Assert.Single(await first.Journal().ListReservationsAsync(null, 10, default));
        Assert.Single(await second.Journal().ListReservationsAsync(null, 10, default));
        // Even a packet signed by this provider's trusted Core cannot cross the business boundary.
        var foreignClaim = second.Core.Signing.Claims[0] with { OrganizationId = first.Enrollment.OrganizationId };
        var foreignPacket = second.Packet with { Authorization = await second.Core.Signing.SignAsync(foreignClaim, default) };
        Assert.Throws<UnauthorizedAccessException>(() => second.Verifier.Verify(foreignPacket));
    }
}
