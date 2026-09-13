using CSweet.Compute.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace CSweet.Compute.HyperV;

internal static class ComputeWindowsService
{
    public const string ServiceName = "CSweet.Compute.HyperV";
    public static string ConfigurationPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CSweet", "Compute", "provider.json");

    public static async Task<int> RunAsync(CancellationToken token = default)
    {
        var state = new MaintenanceServiceState();
        using var host = CreateHost(async (report, cancellation) =>
        {
            var configuration = await ComputeProviderConfigurationLoader.ReadAsync(ConfigurationPath, cancellation);
            ComputeInstallationPreflight.ValidateServiceConfiguration(configuration);
            await HyperVMaintenanceHost.RunAsync(configuration, report, cancellation);
        }, state);
        await host.RunAsync(token);
        return state.Failed ? 1 : 0;
    }

    internal static IHost CreateHost(Func<Action<string>, CancellationToken, Task> run, MaintenanceServiceState state)
    {
        // Trust configuration comes only from the fixed protected file, never environment/appsettings/SCM arguments.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(3));
        builder.Services.AddHostedService(services => new MaintenanceService(run, state,
            services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<IHostLifetime>(),
            services.GetRequiredService<ILogger<MaintenanceService>>()));
        return builder.Build();
    }

    internal sealed class MaintenanceServiceState
    {
        private int failed;
        public bool Failed => Volatile.Read(ref failed) != 0;
        public void MarkFailed() => Volatile.Write(ref failed, 1);
    }

    private sealed class MaintenanceService(Func<Action<string>, CancellationToken, Task> run, MaintenanceServiceState state,
        IHostApplicationLifetime application, IHostLifetime lifetime, ILogger<MaintenanceService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await run(code => logger.LogWarning("Compute maintenance: {MaintenanceCode}", code), stoppingToken);
                if (stoppingToken.IsCancellationRequested) return;
                Fail();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception) { Fail(); }
        }

        private void Fail()
        {
            state.MarkFailed();
            if (OperatingSystem.IsWindows() && lifetime is WindowsServiceLifetime windows) windows.ExitCode = 1;
            // No raw exceptions or protected configuration values are sent to Event Log.
            try { logger.LogCritical("compute-maintenance-service-failed"); }
            finally { application.StopApplication(); }
        }
    }
}
