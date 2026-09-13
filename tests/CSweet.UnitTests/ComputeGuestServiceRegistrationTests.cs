using CSweet.Compute.HyperV;

namespace CSweet.UnitTests;

public sealed class ComputeGuestServiceRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Another guest service")]
    public void Missing_or_conflicting_registration_fails_preflight(string? actual)
    {
        Assert.Throws<InvalidDataException>(() => ComputeGuestServiceRegistration.Validate(_ => actual));
    }

    [Fact]
    public void Preflight_reads_only_the_fixed_compute_service_registration()
    {
        var paths = new List<string>();
        ComputeGuestServiceRegistration.Validate(path => { paths.Add(path); return ComputeGuestServiceRegistration.ElementName; });
        Assert.Equal(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000acb-facb-11e6-bd58-64006a7986d3", Assert.Single(paths));
    }
}
