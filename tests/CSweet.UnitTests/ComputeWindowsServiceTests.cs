using CSweet.Compute.HyperV;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CSweet.UnitTests;

public sealed class ComputeWindowsServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_completion_or_failure_stops_host_and_records_failure(bool throws)
    {
        var state = new ComputeWindowsService.MaintenanceServiceState();
        using var host = ComputeWindowsService.CreateHost((_, _) =>
        {
            if (throws) throw new IOException("Protected detail must not be logged.");
            return Task.CompletedTask;
        }, state);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.RunAsync(timeout.Token);
        Assert.True(state.Failed);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task Service_stop_cancels_and_joins_maintenance_without_recording_failure()
    {
        var state = new ComputeWindowsService.MaintenanceServiceState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = ComputeWindowsService.CreateHost(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
        }, state);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = host.RunAsync(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await running;
        Assert.True(exited.Task.IsCompleted); Assert.False(state.Failed);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public void Service_configuration_cannot_load_environment_or_appsettings_trust_overrides()
    {
        using var host = ComputeWindowsService.CreateHost((_, _) => Task.CompletedTask, new());
        var configuration = Assert.IsAssignableFrom<IConfigurationRoot>(host.Services.GetRequiredService<IConfiguration>());
        Assert.DoesNotContain(configuration.Providers, provider => provider.GetType().Name.Contains("EnvironmentVariables", StringComparison.Ordinal));
        Assert.DoesNotContain(configuration.Providers, provider => provider.GetType().Name.Contains("Json", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSweet", "Compute", "provider.json"), ComputeWindowsService.ConfigurationPath);
    }
}
