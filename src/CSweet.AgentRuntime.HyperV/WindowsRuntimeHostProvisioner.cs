using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace CSweet.AgentRuntime.HyperV;

public sealed class WindowsRuntimeHostProvisioner : IWindowsRuntimeHostProvisioner
{
    internal const string DeveloperBootstrapEnvironmentVariable = "CSWEET_WINDOWS_ISOLATION_BOOTSTRAP";
    internal static readonly TimeSpan LegacyProgressHeartbeatTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ExpectedPhaseGracePeriod = TimeSpan.FromMinutes(2);
    private readonly object _progressLock = new();
    private string? _activeProgressPath;

    public WindowsRuntimeHostProvisioningInfo GetProvisioningInfo()
    {
        if (!OperatingSystem.IsWindows())
            return Unavailable("Secure local agent setup is currently available only on Windows.");
        if (!Environment.UserInteractive)
            return Unavailable("Open C-Sweet in an interactive Windows session to continue secure agent setup.");
        if (GetProgress() is { } progress && IsActiveProgress(progress))
        {
            return new WindowsRuntimeHostProvisioningInfo(
                WindowsRuntimeHostProvisioningMode.Unavailable,
                false,
                "Secure agent runtime preparation is running",
                progress.Message);
        }
        if (TryResolveInstaller(out _))
        {
            return new WindowsRuntimeHostProvisioningInfo(
                WindowsRuntimeHostProvisioningMode.PackagedInstaller,
                true,
                "Install secure agent runtime",
                "C-Sweet will request administrator approval once, verify the bundled signed runtime, and install the RuntimeHost service.");
        }
        if (TryResolveDeveloperBootstrap(out _))
        {
            return new WindowsRuntimeHostProvisioningInfo(
                WindowsRuntimeHostProvisioningMode.DeveloperBootstrap,
                true,
                "Prepare secure agent runtime",
                "C-Sweet will request administrator approval once, then build, test, sign, and install a development-only secure runtime. The first preparation downloads several gigabytes and can take tens of minutes.");
        }
        return Unavailable(
            "This build does not include a signed Windows runtime, and no source-development bootstrap was configured.");
    }

    public WindowsRuntimeHostProvisioningProgress? GetProgress()
    {
        string? preferred;
        lock (_progressLock) preferred = _activeProgressPath;
        var progress = WindowsRuntimeHostProgressStore.ReadLatest(preferred);
        if (progress is not { State: WindowsRuntimeHostProvisioningState.Running }) return progress;
        var bootedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (progress.StartedAt < bootedAt.AddMinutes(-1))
        {
            return progress with
            {
                State = WindowsRuntimeHostProvisioningState.Failed,
                PhaseKey = "setup-interrupted",
                PhaseDisplayName = "Secure runtime preparation was interrupted",
                Message = "Windows restarted before this preparation completed. Start the guided preparation again to resume safely.",
                ErrorCode = "preparation-interrupted",
                ErrorMessage = "The previous preparation did not complete before Windows restarted."
            };
        }
        if (HasExceededExpectedPhaseWindow(progress))
        {
            return progress with
            {
                State = WindowsRuntimeHostProvisioningState.Failed,
                PhaseKey = "setup-timeout",
                PhaseDisplayName = "Secure runtime preparation paused",
                Message = "This preparation step took longer than expected. It is safe to continue it again.",
                ErrorCode = "preparation-timeout",
                ErrorMessage = "The preparation process stopped reporting progress. Choose Continue secure setup to retry safely."
            };
        }
        if (IsOwnerProcessRunning(progress)) return progress;
        return progress with
        {
            State = WindowsRuntimeHostProvisioningState.Failed,
            PhaseKey = "setup-stopped",
            PhaseDisplayName = "Secure runtime preparation paused",
            Message = "The previous preparation ended before setup completed. It is safe to continue this step.",
            ErrorCode = "preparation-stopped",
            ErrorMessage = "The preparation process is no longer running. Choose Continue secure setup to resume safely."
        };
    }

    public Task<WindowsRuntimeHostInstallResult> LaunchInstallerAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Failure("unsupported_host", "RuntimeHost installation is currently available only on Windows."));
        if (!Environment.UserInteractive)
            return Task.FromResult(Failure("interactive_session_required", "Open C-Sweet in an interactive Windows session to install RuntimeHost."));
        if (GetProgress() is { } progress && IsActiveProgress(progress))
            return Task.FromResult(Failure("preparation_already_running", "Secure runtime preparation is already running."));
        if (TryResolveInstaller(out var installer))
            return Task.FromResult(LaunchElevated(installer, WindowsRuntimeHostProvisioningMode.PackagedInstaller));
        if (TryResolveDeveloperBootstrap(out var bootstrap))
            return Task.FromResult(LaunchElevated(bootstrap, WindowsRuntimeHostProvisioningMode.DeveloperBootstrap));
        return Task.FromResult(Failure("installer_payload_missing",
            "This build does not contain a signed Windows runtime and has no source-development bootstrap configured."));
    }

    [SupportedOSPlatform("windows")]
    private WindowsRuntimeHostInstallResult LaunchElevated(
        string script,
        WindowsRuntimeHostProvisioningMode mode)
    {
        var progressJobId = Guid.NewGuid();
        var progressPath = WindowsRuntimeHostProgressStore.CreatePath(progressJobId);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return Failure("powershell_unavailable", "Windows PowerShell is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-ControlPlaneUserSid");
        start.ArgumentList.Add(WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable."));
        start.ArgumentList.Add("-ProgressPath");
        start.ArgumentList.Add(progressPath);
        start.ArgumentList.Add("-ProgressJobId");
        start.ArgumentList.Add(progressJobId.ToString("D"));
        if (mode == WindowsRuntimeHostProvisioningMode.PackagedInstaller)
        {
            start.ArgumentList.Add("-PayloadRoot");
            start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(script)!, "payload"));
        }
        else
        {
            start.ArgumentList.Add("-NoElevation");
        }
        try
        {
            using var process = Process.Start(start);
            if (process is not null)
            {
                lock (_progressLock) _activeProgressPath = progressPath;
            }
            return process is null
                ? Failure("installer_start_failed", "The elevated RuntimeHost installer could not be started.")
                : new WindowsRuntimeHostInstallResult(true, null,
                    mode == WindowsRuntimeHostProvisioningMode.DeveloperBootstrap
                        ? "Windows opened the guided development preparation. Approve the administrator prompt and leave the preparation window open; C-Sweet will recheck automatically."
                        : "Windows opened the secure runtime installer. Approve the administrator prompt and leave the installer open; C-Sweet will recheck automatically.",
                    true);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Failure("elevation_cancelled", "The administrator prompt was cancelled. RuntimeHost was not changed.");
        }
        catch (Win32Exception)
        {
            return Failure("installer_start_failed", "Windows could not open the RuntimeHost installer.");
        }
    }

    internal static bool TryResolveInstaller(out string path)
    {
        var configured = Environment.GetEnvironmentVariable("CSWEET_WINDOWS_RUNTIME_INSTALLER");
        var candidate = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "windows-runtime", "Install-CSweetRuntimeHost.ps1")
            : configured;
        path = string.Empty;
        if (!Path.IsPathFullyQualified(candidate)) return false;
        candidate = Path.GetFullPath(candidate);
        if (!File.Exists(candidate) ||
            !Path.GetExtension(candidate).Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "CSweet.WindowsSetupProgress.ps1")) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "payload", "runtime-manifest.json")))
            return false;
        path = candidate;
        return true;
    }

    internal static bool TryResolveDeveloperBootstrap(out string path)
    {
        var configured = Environment.GetEnvironmentVariable(DeveloperBootstrapEnvironmentVariable);
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured)) return false;
        var candidate = Path.GetFullPath(configured);
        if (!File.Exists(candidate) ||
            !Path.GetExtension(candidate).Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidate).Equals("Initialize-CSweetWindowsIsolationTest.ps1", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "CSweet.WindowsSetupProgress.ps1")) ||
            (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            return false;
        path = candidate;
        return true;
    }

    private static WindowsRuntimeHostProvisioningInfo Unavailable(string reason) => new(
        WindowsRuntimeHostProvisioningMode.Unavailable,
        false,
        "Prepare secure agent runtime",
        reason,
        reason);

    private static bool IsActiveProgress(WindowsRuntimeHostProvisioningProgress progress)
    {
        return progress.State == WindowsRuntimeHostProvisioningState.Running;
    }

    private static bool IsOwnerProcessRunning(WindowsRuntimeHostProvisioningProgress progress)
    {
        if (progress.OwnerProcessId is not { } processId)
            return progress.UpdatedAt > DateTimeOffset.UtcNow - LegacyProgressHeartbeatTimeout;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            var processStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return processStartedAt <= progress.StartedAt &&
                   progress.StartedAt - processStartedAt <= TimeSpan.FromMinutes(2);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return progress.UpdatedAt > DateTimeOffset.UtcNow - LegacyProgressHeartbeatTimeout;
        }
    }

    internal static bool HasExceededExpectedPhaseWindow(WindowsRuntimeHostProvisioningProgress progress)
    {
        if (progress.EstimatedRemainingMaximumSeconds is not { } maximumSeconds)
            return false;
        var expectedWindow = TimeSpan.FromSeconds(Math.Clamp(maximumSeconds, 1, 86_400)) +
            ExpectedPhaseGracePeriod;
        return progress.UpdatedAt < DateTimeOffset.UtcNow - expectedWindow;
    }

    private static WindowsRuntimeHostInstallResult Failure(string code, string message) =>
        new(false, code, message, false);
}
