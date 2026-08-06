using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.HyperV;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentIsolationOnboardingService(
    IWindowsHyperVHostProbe hostProbe,
    IWindowsHyperVFeatureProvisioner featureProvisioner,
    IWindowsRuntimeHostProvisioner runtimeHostProvisioner,
    IEnumerable<IAgentIsolationProvider> isolationProviders,
    IAuditEventWriter auditWriter,
    ILogger<AgentIsolationOnboardingService> logger) : IAgentIsolationOnboardingService
{
    public const string DocumentationUrl =
        "https://learn.microsoft.com/windows-server/virtualization/hyper-v/get-started/install-hyper-v";
    public const string ManualEnableCommand =
        "DISM /Online /Enable-Feature /All /FeatureName:Microsoft-Hyper-V /NoRestart";

    private readonly IAgentIsolationProvider? _hyperVProvider = isolationProviders.FirstOrDefault(provider =>
        string.Equals(provider.Descriptor.ProviderId, IsolationProviderCatalog.HyperV().ProviderId,
            StringComparison.Ordinal));

    public async Task<AgentIsolationOnboardingResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var host = await hostProbe.ProbeAsync(cancellationToken);
        var provisioning = runtimeHostProvisioner.GetProvisioningInfo();
        var progress = runtimeHostProvisioner.GetProgress();
        if (progress is { State: WindowsRuntimeHostProvisioningState.RestartRequired } && !host.IsRestartPending)
            progress = null;
        IsolationProviderProbeResult? providerProbe = null;
        string? providerError = null;
        if (_hyperVProvider is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                providerProbe = await _hyperVProvider.ProbeAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A first-run RuntimeHost probe normally reaches its short timeout before the
                // service is installed. Treat that as an incomplete setup step, not a failure.
                providerError = null;
                logger.LogInformation("RuntimeHost readiness probe timed out before setup completed.");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                                               TimeoutException or UnauthorizedAccessException)
            {
                providerError = Sanitize(exception.Message);
                logger.LogWarning(exception,
                    "RuntimeHost readiness probe did not complete. Error type: {ErrorType}.",
                    exception.GetType().Name);
            }
        }

        var featureEnabled = host.FeatureState is WindowsOptionalFeatureState.Enabled or
            WindowsOptionalFeatureState.EnablePending || host.IsHypervisorPresent;
        var runtimeHostReachable = providerProbe is not null;
        var requiredCapabilities = new IsolationCapabilityRequirements(
            IsolationAssurance.CertifiedHardwareVirtualMachine);
        var capabilitiesReady = providerProbe?.Descriptor.Capabilities.Satisfies(requiredCapabilities) == true;
        var certificationActive = capabilitiesReady && providerProbe is
        {
            IsAvailable: true,
            Certification: not null
        } && providerProbe.Certification.IsActiveAt(DateTimeOffset.UtcNow);
        var installationCompleted = progress is { State: WindowsRuntimeHostProvisioningState.Completed };
        var ready = host.IsWindows && host.IsSupportedEdition && host.HardwareRequirementsSatisfied &&
                    featureEnabled && host.IsHypervisorPresent && !host.IsRestartPending && certificationActive;

        var checks = new List<AgentIsolationOnboardingCheckResponse>
        {
            HostCheck(host),
            EditionCheck(host),
            HardwareCheck(host),
            FeatureCheck(host, featureEnabled),
            RestartCheck(host),
            RuntimeHostCheck(runtimeHostReachable, installationCompleted),
            ProviderCheck(providerProbe, capabilitiesReady, installationCompleted)
        };

        var summary = ready
            ? "Hyper-V isolation is installed, reachable, and certified for untrusted agents."
            : progress is { State: WindowsRuntimeHostProvisioningState.Running }
                ? $"Secure agent runtime preparation is {progress.PercentComplete}% complete: {progress.PhaseDisplayName}."
                : installationCompleted
                    ? "Secure runtime installation is complete. C-Sweet is performing final validation."
                : "C-Sweet remains available, but untrusted agent execution is disabled until every isolation check passes.";

        return new AgentIsolationOnboardingResponse(
            IsolationProviderCatalog.HyperV().ProviderId,
            IsolationProviderCatalog.HyperV().DisplayName,
            host.ProductName,
            host.EditionId,
            host.IsWindows && host.IsSupportedEdition,
            host.HardwareRequirementsSatisfied,
            featureEnabled,
            host.IsHypervisorPresent,
            host.IsRestartPending,
            runtimeHostReachable,
            certificationActive,
            ready,
            host.IsWindows && host.IsSupportedEdition && host.HardwareRequirementsSatisfied &&
                !featureEnabled && host.CanLaunchElevation &&
                provisioning.Mode != WindowsRuntimeHostProvisioningMode.DeveloperBootstrap,
            host.IsWindows && host.IsSupportedEdition && host.CanLaunchElevation &&
                provisioning.CanLaunch && !host.IsRestartPending && !certificationActive &&
                !installationCompleted &&
                (featureEnabled || provisioning.Mode == WindowsRuntimeHostProvisioningMode.DeveloperBootstrap),
            ProvisioningMode(provisioning.Mode),
            provisioning.ActionLabel,
            provisioning.Description,
            summary,
            DocumentationUrl,
            ProgressResponse(progress),
            checks);
    }

    public async Task<AgentIsolationOnboardingActionResponse> EnableHostHypervisorAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await featureProvisioner.LaunchEnablementAsync(cancellationToken);
        await auditWriter.WriteAsync(
            result.Succeeded
                ? "agent-isolation.hyperv.enablement.requested"
                : "agent-isolation.hyperv.enablement.rejected",
            "WindowsHost",
            null,
            result.Message,
            cancellationToken: cancellationToken);
        var status = await GetStatusAsync(cancellationToken);
        return new AgentIsolationOnboardingActionResponse(
            result.Succeeded,
            result.ErrorCode,
            result.Message,
            result.ElevationPromptStarted,
            status);
    }

    public async Task<AgentIsolationOnboardingActionResponse> InstallWindowsRuntimeHostAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await runtimeHostProvisioner.LaunchInstallerAsync(cancellationToken);
        await auditWriter.WriteAsync(
            result.Succeeded
                ? "agent-isolation.runtime-host.installation.requested"
                : "agent-isolation.runtime-host.installation.rejected",
            "WindowsHost",
            null,
            result.Message,
            cancellationToken: cancellationToken);
        var status = await GetStatusAsync(cancellationToken);
        return new AgentIsolationOnboardingActionResponse(
            result.Succeeded, result.ErrorCode, result.Message,
            result.ElevationPromptStarted, status);
    }

    private static AgentIsolationOnboardingCheckResponse HostCheck(WindowsHyperVHostReadiness host) =>
        host.IsWindows
            ? Passed("host", "Windows host", $"Detected {host.ProductName}.")
            : Required("host", "Windows host", "This local provider requires Windows.",
                "Use a future certified remote runner on this host.");

    private static AgentIsolationOnboardingCheckResponse EditionCheck(WindowsHyperVHostReadiness host) =>
        host.IsSupportedEdition
            ? Passed("edition", "Supported Windows edition", $"Detected edition {host.EditionId}.")
            : Required("edition", "Supported Windows edition",
                $"Windows edition {host.EditionId} does not provide the required Hyper-V role.",
                "Upgrade to a supported Professional, Enterprise, or Education edition, or use a certified remote runner.");

    private static AgentIsolationOnboardingCheckResponse HardwareCheck(WindowsHyperVHostReadiness host)
    {
        if (host.HardwareRequirementsSatisfied)
            return Passed("hardware", "Hardware virtualization", "CPU, firmware, memory, and DEP requirements are available.");
        var missing = new List<string>();
        if (!host.HasSecondLevelAddressTranslation) missing.Add("SLAT");
        if (!host.IsVirtualizationEnabledInFirmware) missing.Add("firmware virtualization");
        if (!host.IsDataExecutionPreventionEnabled) missing.Add("hardware DEP/NX");
        if (host.PhysicalMemoryBytes < 4L * 1024 * 1024 * 1024) missing.Add("4 GB RAM");
        return Required("hardware", "Hardware virtualization",
            $"Missing or disabled: {string.Join(", ", missing)}.",
            "Open the computer's UEFI/BIOS settings and enable Intel VT-x/VT-d or AMD-V/SVM, then start Windows and recheck.");
    }

    private static AgentIsolationOnboardingCheckResponse FeatureCheck(
        WindowsHyperVHostReadiness host,
        bool featureEnabled) => featureEnabled
        ? new AgentIsolationOnboardingCheckResponse(
            "hyperv-feature", "Hyper-V Windows feature",
            host.FeatureState == WindowsOptionalFeatureState.EnablePending ? "pending" : "passed",
            host.FeatureState == WindowsOptionalFeatureState.EnablePending
                ? "Hyper-V enablement is pending a Windows restart."
                : "The Hyper-V Windows feature is enabled.",
            host.FeatureState == WindowsOptionalFeatureState.EnablePending ? "Restart Windows, then recheck." : null)
        : Required("hyperv-feature", "Hyper-V Windows feature",
            "The Hyper-V Windows feature is not enabled.",
            $"Choose Enable Hyper-V below, or run as Administrator: {ManualEnableCommand}");

    private static AgentIsolationOnboardingCheckResponse RestartCheck(WindowsHyperVHostReadiness host) =>
        host.IsRestartPending
            ? new AgentIsolationOnboardingCheckResponse("restart", "Hyper-V restart", "pending",
                "Hyper-V needs a Windows restart before isolated agents can run.",
                "Save your work, restart Windows, then reopen C-Sweet.")
            : Passed("restart", "Hyper-V restart", "No Hyper-V restart is required.");

    private static AgentIsolationOnboardingCheckResponse RuntimeHostCheck(
        bool reachable,
        bool installationCompleted) =>
        reachable
            ? Passed("runtime-host", "Privileged RuntimeHost service", "The authenticated local RuntimeHost service is reachable.")
            : installationCompleted
                ? Required("runtime-host", "Privileged RuntimeHost service",
                    "Installation is complete. C-Sweet is validating the RuntimeHost connection.",
                    "This normally completes automatically after the Windows service finishes starting.")
            : Required("runtime-host", "Privileged RuntimeHost service",
                "Next step: prepare the secure agent runtime.",
                "C-Sweet will install and start RuntimeHost, prepare the signed guest image, and verify the local security boundary.");

    private static AgentIsolationOnboardingCheckResponse ProviderCheck(
        IsolationProviderProbeResult? probe,
        bool capabilitiesReady,
        bool installationCompleted)
    {
        if (capabilitiesReady && probe is { IsAvailable: true, Certification: not null })
            return Passed("provider-certification", "Signed guest and provider certification",
                $"Provider certification {probe.Certification.CertificationSuiteVersion} is active.");
        if (installationCompleted)
            return Required("provider-certification", "Signed guest and provider certification",
                "C-Sweet is completing the final security validation.",
                "No separate action is needed; this check follows RuntimeHost validation automatically.");
        if (probe is not null && !capabilitiesReady)
            return Required("provider-certification", "Signed guest and provider certification",
                "This security check will complete during secure runtime preparation.",
                "C-Sweet will verify the signed guest image and all required isolation capabilities automatically.");
        return Required("provider-certification", "Signed guest and provider certification",
            "This security check will complete during secure runtime preparation.",
            "No separate action is needed; it is included with the RuntimeHost preparation step.");
    }

    private static AgentIsolationOnboardingCheckResponse Passed(string key, string name, string message) =>
        new(key, name, "passed", message);

    private static AgentIsolationOnboardingCheckResponse Required(
        string key, string name, string message, string remediation) =>
        new(key, name, "action-required", message, remediation);

    private static string Sanitize(string value) =>
        new(value.Where(character => !char.IsControl(character)).Take(256).ToArray());

    private static string ProvisioningMode(WindowsRuntimeHostProvisioningMode mode) => mode switch
    {
        WindowsRuntimeHostProvisioningMode.PackagedInstaller => "packaged-installer",
        WindowsRuntimeHostProvisioningMode.DeveloperBootstrap => "developer-bootstrap",
        _ => "unavailable"
    };

    private static AgentIsolationProvisioningProgressResponse? ProgressResponse(
        WindowsRuntimeHostProvisioningProgress? progress) => progress is null ? null : new(
        progress.JobId,
        progress.Workflow,
        ProgressState(progress.State),
        progress.PhaseKey,
        progress.PhaseDisplayName,
        progress.Message,
        progress.PercentComplete,
        progress.StartedAt,
        progress.UpdatedAt,
        progress.EstimatedRemainingMinimumSeconds,
        progress.EstimatedRemainingMaximumSeconds,
        progress.RequiresRestart,
        progress.ErrorCode,
        progress.ErrorMessage);

    private static string ProgressState(WindowsRuntimeHostProvisioningState state) => state switch
    {
        WindowsRuntimeHostProvisioningState.RestartRequired => "restart-required",
        WindowsRuntimeHostProvisioningState.Completed => "completed",
        WindowsRuntimeHostProvisioningState.Failed => "failed",
        _ => "running"
    };
}
