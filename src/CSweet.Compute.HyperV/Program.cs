using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "repair-local")
        {
            try { await LocalComputeInstaller.ConfigureAsync(null, args[1], args[2], default); return 0; }
            catch (Exception error) { Console.Error.WriteLine("compute-local-repair-failed: " + error.GetType().Name); return 1; }
        }
        if (args.Length == 4 && args[0] == "configure-local")
        {
            try { await LocalComputeInstaller.ConfigureAsync(args[1], args[2], args[3], default); return 0; }
            catch (Exception error) { Console.Error.WriteLine("compute-local-configuration-failed: " + error.GetType().Name); return 1; }
        }
        if (args.Length == 2 && args[0] == "complete-local")
        {
            try { await LocalComputeInstaller.CompleteAsync(args[1], true, default); return 0; }
            catch (Exception) { Console.Error.WriteLine("compute-local-completion-failed"); return 1; }
        }
        if (args.Length == 1 && args[0] == "validate-service")
        {
            if (!OperatingSystem.IsWindows()) return 2;
            try { await ComputeInstallationPreflight.ValidateServiceAsync(default); return 0; }
            catch (Exception) { Console.Error.WriteLine("compute-service-preflight-failed"); return 1; }
        }
        if (args.Length == 2 && args[0] == "initialize-journal" && Path.IsPathFullyQualified(args[1]))
        {
            if (!OperatingSystem.IsWindows()) return 2;
            try
            {
                var configuration = await ComputeProviderConfigurationLoader.ReadAsync(args[1], default);
                await new ComputeReplayJournal(configuration.JournalDirectory, configuration.Enrollment, configuration.Capacity,
                    TimeProvider.System).InitializeAsync(default);
                return 0;
            }
            catch (Exception) { Console.Error.WriteLine("compute-journal-initialization-failed"); return 1; }
        }
        if (args.Length == 1 && args[0] == "service")
        {
            if (!OperatingSystem.IsWindows() || !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            { Console.Error.WriteLine("windows-service-manager-required"); return 2; }
            try { return await ComputeWindowsService.RunAsync(); }
            catch (Exception) { Console.Error.WriteLine("compute-maintenance-service-failed"); return 1; }
        }
        if (args.Length != 2 || args[0] != "maintenance" || !Path.IsPathFullyQualified(args[1]))
        {
            Console.Error.WriteLine("Usage: CSweet.Compute.HyperV maintenance <absolute protected configuration path>");
            return 2;
        }
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("hyperv-platform-required"); return 2; }
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var configuration = await ComputeProviderConfigurationLoader.ReadAsync(args[1], shutdown.Token);
            await HyperVMaintenanceHost.RunAsync(configuration, Console.Error.WriteLine, shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        catch (Exception) { Console.Error.WriteLine("compute-maintenance-host-failed"); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
