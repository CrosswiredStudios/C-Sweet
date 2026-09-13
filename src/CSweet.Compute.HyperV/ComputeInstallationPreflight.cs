using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

internal static class ComputeInstallationPreflight
{
    public static void ValidateServiceConfiguration(ComputeProviderConfiguration configuration)
    {
        if (configuration.CertificateStoreLocation != StoreLocation.LocalMachine)
            throw new InvalidDataException("The LocalSystem service requires its enrolled certificate in LocalMachine My.");
    }

    public static async Task ValidateServiceAsync(CancellationToken token)
    {
        var configuration = await ComputeProviderConfigurationLoader.ReadAsync(ComputeWindowsService.ConfigurationPath, token);
        ValidateServiceConfiguration(configuration);
        // Validate every installed runtime entry before service registration. This does not certify a guest image.
        var pending = new Stack<string>(); pending.Push(AppContext.BaseDirectory);
        while (pending.TryPop(out var directory))
        {
            WindowsComputeProtectedPaths.Verify(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); WindowsComputeProtectedPaths.Verify(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
        if (configuration.ProvisioningSettingsPath is { } provisioningPath)
        {
            // Explicit installer validation fails on bad settings; normal startup retains maintenance fallback.
            var catalog = await ComputeProvisioningSettingsLoader.ReadCatalogAsync(provisioningPath, configuration, TimeProvider.System, token);
            await catalog.ValidateInstallationAsync(AppContext.BaseDirectory, token);
            ComputeGuestServiceRegistration.Validate();
        }
        var journal = new ComputeReplayJournal(configuration.JournalDirectory, configuration.Enrollment, configuration.Capacity, TimeProvider.System);
        await journal.ListReservationsAsync(null, 1, token); // Never initialize or reset in preflight.
        using var certificate = ComputeProviderConfigurationLoader.OpenSigningCertificate(configuration, TimeProvider.System);
        await new HyperVCommandRunner().RunAsync("Import-Module Hyper-V -ErrorAction Stop; Get-VM -ErrorAction Stop | Out-Null", new Dictionary<string, string>(), token);
    }
}
