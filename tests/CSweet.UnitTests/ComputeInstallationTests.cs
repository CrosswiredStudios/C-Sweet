using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeInstallationTests
{
    [Theory]
    [InlineData(StoreLocation.LocalMachine, true)]
    [InlineData(StoreLocation.CurrentUser, false)]
    public void LocalSystem_service_cannot_depend_on_the_installing_users_personal_certificate_store(StoreLocation location, bool valid)
    {
        var configuration = new ComputeProviderConfiguration(1, null!, null!, "https://core.example.test", "", "", null!, "", location);
        if (valid) ComputeInstallationPreflight.ValidateServiceConfiguration(configuration);
        else Assert.Throws<InvalidDataException>(() => ComputeInstallationPreflight.ValidateServiceConfiguration(configuration));
    }

    [Fact]
    public async Task Initialization_command_cannot_create_state_from_an_unprotected_configuration_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new ComputeReplayJournalTests.Fixture();
        Directory.CreateDirectory(fixture.Root);
        var configurationPath = Path.Combine(fixture.Root, "provider.json");
        await File.WriteAllTextAsync(configurationPath, "{}");
        Assert.Equal(1, await CSweet.Compute.HyperV.Program.Main(["initialize-journal", configurationPath]));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "journal.json")));
        Assert.Equal("{}", await File.ReadAllTextAsync(configurationPath));
    }
}
