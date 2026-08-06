using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.HyperV.Helper;
using CSweet.AgentRuntime.Guest;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class WindowsHyperVOnboardingTests
{
    [Theory]
    [InlineData("Professional", true)]
    [InlineData("ProfessionalWorkstation", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Education", true)]
    [InlineData("ServerStandard", true)]
    [InlineData("Core", false)]
    [InlineData("Home", false)]
    public void SupportedEdition_IsExplicit(string edition, bool expected)
    {
        Assert.Equal(expected, WindowsHyperVHostProbe.IsSupportedEdition(edition));
    }

    [Theory]
    [InlineData("State : Enabled", WindowsOptionalFeatureState.Enabled)]
    [InlineData("State : Disabled", WindowsOptionalFeatureState.Disabled)]
    [InlineData("State : Enable Pending", WindowsOptionalFeatureState.EnablePending)]
    [InlineData("State : Disable Pending", WindowsOptionalFeatureState.DisablePending)]
    [InlineData("unexpected", WindowsOptionalFeatureState.Unknown)]
    public void FeatureStateParser_IsBoundedToKnownStates(
        string output,
        WindowsOptionalFeatureState expected)
    {
        Assert.Equal(expected, WindowsHyperVHostProbe.ParseFeatureState(output));
    }

    [Theory]
    [InlineData(WindowsOptionalFeatureState.EnablePending, false, false, true)]
    [InlineData(WindowsOptionalFeatureState.Enabled, false, true, true)]
    [InlineData(WindowsOptionalFeatureState.Enabled, true, true, false)]
    [InlineData(WindowsOptionalFeatureState.Unknown, false, true, false)]
    [InlineData(WindowsOptionalFeatureState.Disabled, false, true, false)]
    public void RestartPending_BlocksOnlyHyperVRelevantRestart(
        WindowsOptionalFeatureState featureState,
        bool hypervisorPresent,
        bool windowsRestartPending,
        bool expected)
    {
        Assert.Equal(expected, WindowsHyperVHostProbe.IsHyperVRestartPending(
            featureState,
            hypervisorPresent,
            windowsRestartPending));
    }

    [Fact]
    public void HelperArguments_RejectUnknownOperations()
    {
        var exception = Assert.Throws<HelperProtocolException>(() =>
            HelperArguments.Parse(["--protocol", "1.0", "--operation", "execute-command"]));

        Assert.Equal("invalid-arguments", exception.ErrorCode);
    }

    [Fact]
    public void HelperArguments_AcceptOnlyTypedLifecycleOperation()
    {
        var result = HelperArguments.Parse(["--protocol", "1.0", "--operation", "create"]);

        Assert.Equal("1.0", result.ProtocolVersion);
        Assert.Equal("create", result.Operation);
    }

    [Fact]
    public void LinuxVsockServiceId_UsesMicrosoftHyperVGuidTemplate()
    {
        Assert.Equal(
            Guid.Parse("00000ac9-facb-11e6-bd58-64006a7986d3"),
            HyperVSocketTransportOptions.LinuxVsockServiceId(2761));
    }

    [Fact]
    public void HyperVSocketServiceRegistration_UsesUnbracedRegistryKeyName()
    {
        var serviceId = HyperVSocketTransportOptions.LinuxVsockServiceId(2761);

        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000ac9-facb-11e6-bd58-64006a7986d3",
            WindowsHyperVSocketServiceRegistration.ServiceKeyPath(serviceId));
        Assert.EndsWith(
            @"\{00000ac9-facb-11e6-bd58-64006a7986d3}",
            WindowsHyperVSocketServiceRegistration.LegacyBracedServiceKeyPath(serviceId),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxVsockNativeAddress_MatchesSockAddrVmLayout()
    {
        Assert.Equal(16, Marshal.SizeOf<LinuxHyperVSocketGuestTransport.LinuxSockAddrVm>());
    }

    [Fact]
    public async Task LinuxVsockAcceptedHandle_UsesIndependentSynchronousStreams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-vsock-handle-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(path, []);
            var inputHandle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.None);
            var outputHandle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                FileOptions.None);
            await using var connection = LinuxHyperVSocketGuestTransport.OpenAcceptedConnection(
                inputHandle,
                outputHandle);

            Assert.NotSame(connection.Input, connection.Output);
            Assert.False(((FileStream)connection.Input).IsAsync);
            Assert.False(((FileStream)connection.Output).IsAsync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GuestService_KeepsScratchMountInBrokerProcess()
    {
        var provisioningScript = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "build", "windows-hyperv", "provision-guest.sh"));

        Assert.Contains(
            "exec /usr/lib/csweet/guest/CSweet.AgentRuntime.Guest",
            provisioningScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/usr/lib/csweet/prepare-runtime.sh",
            provisioningScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExecStartPre=", provisioningScript, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_DoesNotUseScForQuotedServiceExecutablePath()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetRuntimeHost.ps1"));

        Assert.Contains("New-Service -Name $serviceName -BinaryPathName $binaryPath", installer, StringComparison.Ordinal);
        Assert.Contains("Invoke-CimMethod -InputObject $serviceConfiguration -MethodName Change", installer, StringComparison.Ordinal);
        Assert.Contains("PathName = $binaryPath", installer, StringComparison.Ordinal);
        Assert.Contains("StartName = 'LocalSystem'", installer, StringComparison.Ordinal);
        Assert.Contains("'reset=', '86400'", installer, StringComparison.Ordinal);
        Assert.Contains("'actions=', 'restart/5000/restart/15000/none/0'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Sc @('create'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Sc @('config'", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_SkipsAlreadyInstalledContentByDigest()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetRuntimeHost.ps1"));

        Assert.Contains("Get-FileHash -LiteralPath $destination -Algorithm SHA256", installer, StringComparison.Ordinal);
        Assert.Contains("if ($installedHash -ceq [string]$file.sha256)", installer, StringComparison.Ordinal);
        Assert.Contains("continue", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_UsesUnbracedHyperVSocketRegistration()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetRuntimeHost.ps1"));

        Assert.Contains(
            "$serviceId = '00000ac9-facb-11e6-bd58-64006a7986d3'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $legacyServiceRegistryPath", installer, StringComparison.Ordinal);
        Assert.Contains("AllowedClientSid = $ControlPlaneUserSid", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void DeveloperBootstrap_ResolvesOnlyConfiguredGuidedSetupScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Initialize-CSweetWindowsIsolationTest.ps1");
        File.WriteAllText(path, "# test bootstrap");
        File.WriteAllText(Path.Combine(directory, "CSweet.WindowsSetupProgress.ps1"), "# test progress helper");
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                path);

            Assert.True(WindowsRuntimeHostProvisioner.TryResolveDeveloperBootstrap(out var resolved));
            Assert.Equal(Path.GetFullPath(path), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProgressStore_ReadsLatestValidatedProvisioningProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            var jobId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-4);
            var path = WindowsRuntimeHostProgressStore.CreatePath(jobId);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                jobId,
                workflow = "developer-bootstrap",
                state = "running",
                phaseKey = "build-guest",
                phaseDisplayName = "Building the hardened guest image",
                message = "Ubuntu is installing into the isolated VM.",
                percentComplete = 24,
                startedAt,
                updatedAt = DateTimeOffset.UtcNow,
                estimatedRemainingMinimumSeconds = 900,
                estimatedRemainingMaximumSeconds = 2100,
                requiresRestart = false,
                errorCode = (string?)null,
                errorMessage = (string?)null
            }));

            var progress = WindowsRuntimeHostProgressStore.ReadLatest(null);

            Assert.NotNull(progress);
            Assert.Equal(jobId, progress.JobId);
            Assert.Equal(WindowsRuntimeHostProvisioningState.Running, progress.State);
            Assert.Equal(24, progress.PercentComplete);
            Assert.Equal(900, progress.EstimatedRemainingMinimumSeconds);
            Assert.Equal(2100, progress.EstimatedRemainingMaximumSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provisioner_StaleLegacyProgressBecomesRetryable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var bootstrap = Path.Combine(directory, "Initialize-CSweetWindowsIsolationTest.ps1");
        File.WriteAllText(bootstrap, "# test bootstrap");
        File.WriteAllText(Path.Combine(directory, "CSweet.WindowsSetupProgress.ps1"), "# test progress helper");
        var originalRoot = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        var originalBootstrap = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                bootstrap);
            var jobId = Guid.NewGuid();
            File.WriteAllText(
                WindowsRuntimeHostProgressStore.CreatePath(jobId),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId,
                    workflow = "developer-bootstrap",
                    state = "running",
                    phaseKey = "build-guest",
                    phaseDisplayName = "Building the hardened guest image",
                    message = "Ubuntu is installing.",
                    percentComplete = 24,
                    startedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    updatedAt = DateTimeOffset.UtcNow.Subtract(
                        WindowsRuntimeHostProvisioner.LegacyProgressHeartbeatTimeout).AddSeconds(-5),
                    estimatedRemainingMinimumSeconds = 0,
                    estimatedRemainingMaximumSeconds = 180,
                    requiresRestart = false,
                    errorCode = (string?)null,
                    errorMessage = (string?)null
                }));

            var provisioner = new WindowsRuntimeHostProvisioner();
            var progress = provisioner.GetProgress();
            var info = provisioner.GetProvisioningInfo();

            Assert.NotNull(progress);
            Assert.Equal(WindowsRuntimeHostProvisioningState.Failed, progress.State);
            Assert.Equal("preparation-stopped", progress.ErrorCode);
            Assert.True(info.CanLaunch);
            Assert.Equal(WindowsRuntimeHostProvisioningMode.DeveloperBootstrap, info.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                originalRoot);
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                originalBootstrap);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provisioner_RunningOwnerProcessKeepsProgressActive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            using var owner = System.Diagnostics.Process.GetCurrentProcess();
            var jobId = Guid.NewGuid();
            var ownerStartedAt = new DateTimeOffset(owner.StartTime.ToUniversalTime(), TimeSpan.Zero);
            File.WriteAllText(
                WindowsRuntimeHostProgressStore.CreatePath(jobId),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId,
                    workflow = "developer-bootstrap",
                    state = "running",
                    phaseKey = "build-guest",
                    phaseDisplayName = "Building the hardened guest image",
                    message = "Ubuntu is installing.",
                    percentComplete = 30,
                    startedAt = ownerStartedAt,
                    updatedAt = DateTimeOffset.UtcNow,
                    ownerProcessId = owner.Id,
                    estimatedRemainingMinimumSeconds = 300,
                    estimatedRemainingMaximumSeconds = 1200,
                    requiresRestart = false,
                    errorCode = (string?)null,
                    errorMessage = (string?)null
                }));

            var progress = new WindowsRuntimeHostProvisioner().GetProgress();

            Assert.NotNull(progress);
            Assert.Equal(WindowsRuntimeHostProvisioningState.Running, progress.State);
            Assert.Equal(owner.Id, progress.OwnerProcessId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provisioner_ExpectedPhaseWindowExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new WindowsRuntimeHostProvisioningProgress(
            Guid.NewGuid(),
            "developer-bootstrap",
            WindowsRuntimeHostProvisioningState.Running,
            "certify-runtime",
            "Certifying hardware isolation",
            "A disposable VM is running the certification probe.",
            70,
            now.AddMinutes(-20),
            now.AddMinutes(-11),
            120,
            480,
            false,
            null,
            null,
            Environment.ProcessId);

        Assert.True(WindowsRuntimeHostProvisioner.HasExceededExpectedPhaseWindow(progress));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The C-Sweet repository root was not found.");
    }
}
