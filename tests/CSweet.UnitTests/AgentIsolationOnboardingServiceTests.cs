using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.HyperV;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentIsolationOnboardingServiceTests
{
    [Fact]
    public async Task CompletedInstallationThatCannotBeReachedOffersGuidedRepair()
    {
        var service = CreateService(new UnreachableHyperVProvider());

        var status = await service.GetStatusAsync();

        Assert.False(status.IsReady);
        Assert.False(status.IsRuntimeHostReachable);
        Assert.True(status.CanAutomateRuntimeHostInstallation);
        Assert.Equal("Repair secure agent runtime", status.RuntimeHostActionLabel);
        Assert.Contains("administrator approval", status.RuntimeHostActionDescription, StringComparison.Ordinal);
        var runtimeHost = Assert.Single(status.Checks, check => check.Key == "runtime-host");
        Assert.Equal("action-required", runtimeHost.Status);
        Assert.Contains("cannot connect", runtimeHost.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Repair secure agent runtime", runtimeHost.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveCertifiedProviderCompletesEveryIsolationCheck()
    {
        var descriptor = IsolationProviderCatalog.HyperV();
        var certification = new IsolationProviderCertification(
            descriptor.ProviderId,
            descriptor.ProviderVersion,
            descriptor.HostOperatingSystem,
            descriptor.HostArchitecture,
            $"sha256:{new string('a', 64)}",
            "1.0",
            AgentRuntimeManagerOptions.CurrentDevelopmentCertificationSuiteVersion,
            $"sha256:{new string('b', 64)}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var service = CreateService(new AvailableHyperVProvider(descriptor, certification));

        var status = await service.GetStatusAsync();

        Assert.True(status.IsReady);
        Assert.True(status.IsRuntimeHostReachable);
        Assert.True(status.IsProviderCertified);
        Assert.False(status.CanAutomateRuntimeHostInstallation);
        Assert.All(status.Checks, check => Assert.Equal("passed", check.Status));
    }

    [Fact]
    public async Task CertifiedProviderCannotCompleteOnboardingWhileInstallerIsStillRunning()
    {
        var descriptor = IsolationProviderCatalog.HyperV();
        var certification = new IsolationProviderCertification(
            descriptor.ProviderId,
            descriptor.ProviderVersion,
            descriptor.HostOperatingSystem,
            descriptor.HostArchitecture,
            $"sha256:{new string('a', 64)}",
            "1.0",
            AgentRuntimeManagerOptions.CurrentDevelopmentCertificationSuiteVersion,
            $"sha256:{new string('b', 64)}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var service = CreateService(
            new AvailableHyperVProvider(descriptor, certification),
            new RunningRuntimeHostProvisioner());

        var status = await service.GetStatusAsync();

        Assert.False(status.IsReady);
        Assert.True(status.IsRuntimeHostReachable);
        Assert.True(status.IsProviderCertified);
        Assert.Equal("running", status.ProvisioningProgress?.State);
        Assert.Contains("preparation", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            status.Checks.Where(check => check.Key is "runtime-host" or "provider-certification"),
            check => Assert.Equal("action-required", check.Status));
    }

    [Fact]
    public async Task ReachableProviderWithStaleGuestContractOffersGuidedUpdate()
    {
        var descriptor = IsolationProviderCatalog.HyperV();
        var certification = new IsolationProviderCertification(
            descriptor.ProviderId,
            descriptor.ProviderVersion,
            descriptor.HostOperatingSystem,
            descriptor.HostArchitecture,
            $"sha256:{new string('a', 64)}",
            "1.0",
            "csweet-windows-hyperv-smoke-v1",
            $"sha256:{new string('b', 64)}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var service = CreateService(new AvailableHyperVProvider(descriptor, certification));

        var status = await service.GetStatusAsync();

        Assert.False(status.IsReady);
        Assert.True(status.IsRuntimeHostReachable);
        Assert.False(status.IsProviderCertified);
        Assert.True(status.CanAutomateRuntimeHostInstallation);
        Assert.Equal("Update secure agent runtime", status.RuntimeHostActionLabel);
        Assert.Contains("update the secure guest runtime", status.RuntimeHostActionDescription,
            StringComparison.OrdinalIgnoreCase);
        var provider = Assert.Single(status.Checks, check => check.Key == "provider-certification");
        Assert.Equal("action-required", provider.Status);
        Assert.Contains("refreshes", provider.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallActionRepairsAccessOnlyForCompletedUnreachableRuntime()
    {
        var provisioner = new CompletedRuntimeHostProvisioner();
        var service = CreateService(new UnreachableHyperVProvider(), provisioner);

        var result = await service.InstallWindowsRuntimeHostAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(WindowsRuntimeHostProvisioningAction.RepairAccess, provisioner.LastAction);
    }

    [Fact]
    public async Task RejectedInstalledRuntimeOffersFullPreparationInsteadOfRepeatingAccessRepair()
    {
        var provisioner = new CompletedRuntimeHostProvisioner();
        var service = CreateService(new RejectedHyperVProvider(), provisioner);

        var status = await service.GetStatusAsync();
        var result = await service.InstallWindowsRuntimeHostAsync();

        Assert.False(status.IsReady);
        Assert.Equal("developer-bootstrap", status.RuntimeHostProvisioningMode);
        Assert.Equal("Prepare secure agent runtime", status.RuntimeHostActionLabel);
        Assert.Equal(WindowsRuntimeHostProvisioningAction.Prepare, provisioner.LastAction);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OnboardingPageKeepsCompletedValidationPollingAndShowsRepairAction()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.UI", "Setup", "AgentIsolationSetupStep.razor"));
        var runtimeSettings = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.UI", "Pages", "AgentRuntimeSettings.razor"));
        var agents = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.UI", "Pages", "Agents.razor"));

        Assert.Contains("@if (_status.CanAutomateRuntimeHostInstallation)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_status.ProvisioningProgress?.State != \"completed\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("finalValidationDeadline", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AdvanceWhenReadyAsync", source, StringComparison.Ordinal);
        Assert.Contains("_status?.IsReady == true ? \"Continue\" : \"Continue setup later\"", source, StringComparison.Ordinal);
        Assert.Contains("Hyper-V may look empty", source, StringComparison.Ordinal);
        Assert.Contains("Continue to build or run agents", runtimeSettings, StringComparison.Ordinal);
        Assert.Contains("_isSecureRuntimeReady", agents, StringComparison.Ordinal);
    }

    private static AgentIsolationOnboardingService CreateService(
        IAgentIsolationProvider provider,
        IWindowsRuntimeHostProvisioner? provisioner = null) => new(
        new ReadyHostProbe(),
        new NoOpFeatureProvisioner(),
        provisioner ?? new CompletedRuntimeHostProvisioner(),
        [provider],
        new TestAuditEventWriter(),
        NullLogger<AgentIsolationOnboardingService>.Instance);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The C-Sweet repository root was not found.");
    }

    private sealed class ReadyHostProbe : IWindowsHyperVHostProbe
    {
        public Task<WindowsHyperVHostReadiness> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsHyperVHostReadiness(
                true,
                "Windows 11 Pro",
                "Professional",
                true,
                true,
                true,
                true,
                16L * 1024 * 1024 * 1024,
                WindowsOptionalFeatureState.Enabled,
                true,
                false,
                true,
                null));
    }

    private sealed class NoOpFeatureProvisioner : IWindowsHyperVFeatureProvisioner
    {
        public Task<WindowsHyperVEnablementResult> LaunchEnablementAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsHyperVEnablementResult(true, null, "Enabled", false));
    }

    private sealed class CompletedRuntimeHostProvisioner : IWindowsRuntimeHostProvisioner
    {
        public WindowsRuntimeHostProvisioningAction? LastAction { get; private set; }

        public WindowsRuntimeHostProvisioningInfo GetProvisioningInfo(bool preferAccessRepair = false) => new(
            preferAccessRepair
                ? WindowsRuntimeHostProvisioningMode.AccessRepair
                : WindowsRuntimeHostProvisioningMode.DeveloperBootstrap,
            true,
            preferAccessRepair ? "Repair secure agent runtime" : "Prepare secure agent runtime",
            preferAccessRepair ? "Repair RuntimeHost access." : "Prepare the secure runtime.");

        public WindowsRuntimeHostProvisioningProgress GetProgress() => new(
            Guid.NewGuid(),
            "developer-bootstrap",
            WindowsRuntimeHostProvisioningState.Completed,
            "setup-complete",
            "Secure agent runtime ready",
            "RuntimeHost is installed.",
            100,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null,
            null,
            false,
            null,
            null,
            null);

        public Task<WindowsRuntimeHostInstallResult> LaunchInstallerAsync(
            WindowsRuntimeHostProvisioningAction action,
            CancellationToken cancellationToken = default)
        {
            LastAction = action;
            return Task.FromResult(new WindowsRuntimeHostInstallResult(true, null, "Started", true));
        }
    }

    private sealed class RunningRuntimeHostProvisioner : IWindowsRuntimeHostProvisioner
    {
        public WindowsRuntimeHostProvisioningInfo GetProvisioningInfo(bool preferAccessRepair = false) => new(
            WindowsRuntimeHostProvisioningMode.Unavailable,
            false,
            "Secure agent runtime preparation is running",
            "C-Sweet is finishing setup.");

        public WindowsRuntimeHostProvisioningProgress GetProgress() => new(
            Guid.NewGuid(),
            "developer-bootstrap",
            WindowsRuntimeHostProvisioningState.Running,
            "start-service",
            "Starting the RuntimeHost service",
            "The privileged VM lifecycle service is being replaced and started.",
            98,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            5,
            60,
            false,
            null,
            null,
            Environment.ProcessId);

        public Task<WindowsRuntimeHostInstallResult> LaunchInstallerAsync(
            WindowsRuntimeHostProvisioningAction action,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsRuntimeHostInstallResult(
                false,
                "already-running",
                "Secure runtime preparation is already running.",
                false));
    }

    private abstract class TestHyperVProvider : IAgentIsolationProvider
    {
        public abstract IsolationProviderDescriptor Descriptor { get; }
        public abstract Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
        public Task<IsolationWorkloadHandle> CreateAsync(IsolationWorkloadSpec workload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnreachableHyperVProvider : TestHyperVProvider
    {
        public override IsolationProviderDescriptor Descriptor { get; } = IsolationProviderCatalog.HyperV();
        public override Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("The current control-plane identity cannot open the RuntimeHost pipe.");
    }

    private sealed class RejectedHyperVProvider : TestHyperVProvider
    {
        public override IsolationProviderDescriptor Descriptor { get; } = IsolationProviderCatalog.HyperV();
        public override Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            throw new EndOfStreamException("The runtime host returned no response.");
    }

    private sealed class AvailableHyperVProvider(
        IsolationProviderDescriptor descriptor,
        IsolationProviderCertification certification) : TestHyperVProvider
    {
        public override IsolationProviderDescriptor Descriptor { get; } = descriptor;
        public override Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsolationProviderProbeResult(Descriptor, true, null, certification));
    }
}
