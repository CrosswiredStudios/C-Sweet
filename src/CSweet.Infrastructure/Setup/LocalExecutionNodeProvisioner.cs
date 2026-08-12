using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSweet.AgentRuntime.HyperV;
using CSweet.Application.Setup;

namespace CSweet.Infrastructure.Setup;

public sealed class LocalExecutionNodeProvisioner(
    IWindowsRuntimeHostProvisioner windowsProvisioner,
    TimeProvider timeProvider) : ILocalExecutionNodeProvisioner
{
    internal const string ProgressRootEnvironmentVariable = "CSWEET_LOCAL_EXECUTION_PROGRESS_ROOT";
    internal const string LinuxInstallerEnvironmentVariable = "CSWEET_LINUX_EXECUTION_INSTALLER";
    internal const string MacOsInstallerEnvironmentVariable = "CSWEET_MACOS_EXECUTION_INSTALLER";
    private readonly object _progressLock = new();
    private string? _activeProgressPath;

    public LocalExecutionNodeProvisioningProgress? GetProgress()
    {
        if (OperatingSystem.IsWindows())
            return MapWindowsProgress(windowsProvisioner.GetProgress());

        var path = ResolveLatestProgressPath();
        if (path is null) return null;
        var progress = ReadProgress(path);
        if (progress is null) return null;
        var result = ReadResult(progress.ResultPath);
        if (result == "completed" && progress.State != "completed")
        {
            progress = progress with
            {
                State = "completed",
                PhaseKey = "services-started",
                PhaseDisplayName = "Execution services installed",
                Message = "RuntimeHost and ExecutionNode were installed and started. Waiting for the node to connect.",
                PercentComplete = 100,
                UpdatedAt = timeProvider.GetUtcNow(),
                ErrorCode = null,
                ErrorMessage = null
            };
            WriteProgress(path, progress);
        }
        else if (result == "failed" && progress.State == "running")
        {
            progress = Failed(progress, "installer_failed",
                "The elevated execution-node installer did not complete successfully.");
            WriteProgress(path, progress);
        }
        else if (progress.State == "running" && !IsProcessRunning(progress.OwnerProcessId))
        {
            progress = Failed(progress, "installer_interrupted",
                "The local installer stopped before reporting completion. It is safe to create a replacement enrollment and try again.");
            WriteProgress(path, progress);
        }
        return Map(progress);
    }

    public Task<LocalExecutionNodeProvisioningResult> PrepareAsync(
        string controlPlaneUrl,
        string enrollmentToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateEnrollment(controlPlaneUrl, enrollmentToken, out var validationError))
            return Task.FromResult(Failure("invalid_execution_node_enrollment", validationError));

        if (OperatingSystem.IsWindows())
            return PrepareWindowsAsync(controlPlaneUrl, enrollmentToken, cancellationToken);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return Task.FromResult(Failure("unsupported_host",
                "Local execution-node installation requires Windows, Linux, or macOS."));
        if (!Environment.UserInteractive)
            return Task.FromResult(Failure("interactive_session_required",
                "Open C-Sweet in an interactive desktop session to install the local execution node."));

        var platform = OperatingSystem.IsLinux() ? "linux" : "macos";
        if (!TryResolveUnixInstaller(platform, AppContext.BaseDirectory, out var installer, out var packageRoot))
            return Task.FromResult(Failure("installer_payload_missing",
                $"This build does not contain a complete signed {platform} execution-node payload."));

        var running = GetProgress();
        if (running?.State == "running")
            return Task.FromResult(Failure("preparation_already_running",
                "Local execution-node preparation is already running."));

        try
        {
            var jobId = Guid.NewGuid();
            var startedAt = timeProvider.GetUtcNow();
            var progressRoot = GetProgressRoot();
            Directory.CreateDirectory(progressRoot);
            RestrictDirectory(progressRoot);
            var secretPath = Path.Combine(progressRoot, $"enrollment-{jobId:N}.secret");
            var resultPath = UnixResultPath(platform, jobId);
            var progressPath = Path.Combine(progressRoot, $"local-provisioning-{jobId:N}.json");
            WriteProtectedFile(secretPath, enrollmentToken);

            var start = OperatingSystem.IsLinux()
                ? LinuxStartInfo(installer, packageRoot, controlPlaneUrl, secretPath, jobId)
                : MacOsStartInfo(installer, packageRoot, controlPlaneUrl, secretPath, jobId);
            using var process = Process.Start(start);
            if (process is null)
            {
                DeleteIfExists(secretPath);
                return Task.FromResult(Failure("installer_start_failed",
                    "The privileged execution-node installer could not be started."));
            }

            var stored = new StoredProgress(
                jobId, platform, "running", "installing-services", "Installing execution services",
                "Approve the administrator prompt and leave the installer running. C-Sweet will recheck automatically.",
                35, startedAt, startedAt, false, null, null, process.Id, resultPath);
            WriteProgress(progressPath, stored);
            lock (_progressLock) _activeProgressPath = progressPath;
            _ = ObserveAsync(process.Id, progressPath, secretPath);
            return Task.FromResult(new LocalExecutionNodeProvisioningResult(
                true, null,
                "The privileged local installer was opened. Approve the administrator prompt; C-Sweet will recheck automatically.",
                true));
        }
        catch (Win32Exception)
        {
            return Task.FromResult(Failure("installer_start_failed",
                "The operating system could not open the privileged execution-node installer."));
        }
        catch (IOException)
        {
            return Task.FromResult(Failure("installer_state_failed",
                "C-Sweet could not create protected local installer state."));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Failure("installer_state_failed",
                "C-Sweet could not protect local installer state."));
        }
    }

    private async Task<LocalExecutionNodeProvisioningResult> PrepareWindowsAsync(
        string controlPlaneUrl,
        string enrollmentToken,
        CancellationToken cancellationToken)
    {
        var result = await windowsProvisioner.LaunchInstallerAsync(
            WindowsRuntimeHostProvisioningAction.Prepare,
            controlPlaneUrl,
            enrollmentToken,
            cancellationToken);
        return new LocalExecutionNodeProvisioningResult(
            result.Succeeded, result.ErrorCode, result.Message, result.ElevationPromptStarted);
    }

    private async Task ObserveAsync(int processId, string progressPath, string secretPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync();
            var progress = ReadProgress(progressPath);
            if (progress is null || ReadResult(progress.ResultPath) == "completed") return;
            if (progress.State == "running")
                WriteProgress(progressPath, Failed(progress, "installer_failed",
                    "The elevated execution-node installer did not complete successfully."));
        }
        catch (ArgumentException)
        {
            // GetProgress performs the durable process/result reconciliation.
        }
        finally
        {
            DeleteIfExists(secretPath);
        }
    }

    private ProcessStartInfo LinuxStartInfo(
        string installer,
        string packageRoot,
        string controlPlaneUrl,
        string secretPath,
        Guid jobId)
    {
        var pkexec = new[] { "/usr/bin/pkexec", "/bin/pkexec" }.FirstOrDefault(File.Exists)
            ?? throw new Win32Exception("pkexec is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = pkexec,
            UseShellExecute = false
        };
        AddInstallerArguments(start, installer, packageRoot, controlPlaneUrl, secretPath, jobId);
        return start;
    }

    private static ProcessStartInfo MacOsStartInfo(
        string installer,
        string packageRoot,
        string controlPlaneUrl,
        string secretPath,
        Guid jobId)
    {
        const string osascript = "/usr/bin/osascript";
        if (!File.Exists(osascript)) throw new Win32Exception("osascript is unavailable.");
        var command = string.Join(' ', new[]
        {
            "/bin/sh", installer, packageRoot, controlPlaneUrl,
            "--enrollment-token-file", secretPath,
            "--result-job-id", jobId.ToString("N")
        }.Select(ShellQuote));
        var appleScript = $"do shell script \"{EscapeAppleScript(command)}\" with administrator privileges";
        var start = new ProcessStartInfo
        {
            FileName = osascript,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(appleScript);
        return start;
    }

    private static void AddInstallerArguments(
        ProcessStartInfo start,
        string installer,
        string packageRoot,
        string controlPlaneUrl,
        string secretPath,
        Guid jobId)
    {
        foreach (var argument in new[]
        {
            "/bin/sh", installer, packageRoot, controlPlaneUrl,
            "--enrollment-token-file", secretPath,
            "--result-job-id", jobId.ToString("N")
        })
            start.ArgumentList.Add(argument);
    }

    internal static bool TryResolveUnixInstaller(
        string platform,
        string baseDirectory,
        out string installer,
        out string packageRoot)
    {
        var environmentVariable = platform == "linux"
            ? LinuxInstallerEnvironmentVariable
            : MacOsInstallerEnvironmentVariable;
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        var candidate = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(baseDirectory, $"{platform}-runtime", "install-execution-node.sh")
            : configured;
        installer = string.Empty;
        packageRoot = string.Empty;
        if (!Path.IsPathFullyQualified(candidate)) return false;
        candidate = Path.GetFullPath(candidate);
        if (!File.Exists(candidate) ||
            !Path.GetFileName(candidate).Equals("install-execution-node.sh", StringComparison.Ordinal) ||
            (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            return false;
        var root = Path.GetDirectoryName(candidate)!;
        var required = platform == "linux"
            ? new[]
            {
                "CSweet.RuntimeHost", "CSweet.ExecutionNode", "CSweet.AgentRuntime.Firecracker.Helper",
                "runtime-manifest.json", "csweet-runtime-host.service", "csweet-execution-node.service",
                "uninstall-execution-node.sh",
                Path.Combine("firecracker", "firecracker"), Path.Combine("firecracker", "jailer"),
                Path.Combine("firecracker", "vmlinux"), Path.Combine("firecracker", "initrd.img")
            }
            : new[]
            {
                "CSweet.RuntimeHost", "CSweet.ExecutionNode", "CSweet.AgentRuntime.AppleVirtualization.Helper",
                "runtime-manifest.json", "com.csweet.runtimehost.plist", "com.csweet.executionnode.plist",
                "uninstall-execution-node.sh",
                Path.Combine("apple-virtualization", "vmlinux")
            };
        if (required.Any(relative => !File.Exists(Path.Combine(root, relative)))) return false;
        installer = candidate;
        packageRoot = root;
        return true;
    }

    internal static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    internal static string EscapeAppleScript(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static string UnixResultPath(string platform, Guid jobId) => platform == "linux"
        ? $"/var/lib/csweet/setup/local-provisioning-{jobId:N}.result"
        : $"/Library/Application Support/CSweet/Setup/local-provisioning-{jobId:N}.result";

    private static bool TryValidateEnrollment(string controlPlaneUrl, string token, out string error)
    {
        error = string.Empty;
        if (!Uri.TryCreate(controlPlaneUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The execution gateway must be an HTTPS origin without credentials, query, or fragment.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256 ||
            token.Any(char.IsWhiteSpace))
        {
            error = "The one-use enrollment token is invalid.";
            return false;
        }
        return true;
    }

    private string? ResolveLatestProgressPath()
    {
        lock (_progressLock)
            if (_activeProgressPath is { } active && File.Exists(active)) return active;
        var root = GetProgressRoot();
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "local-provisioning-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string GetProgressRoot()
    {
        var configured = Environment.GetEnvironmentVariable(ProgressRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
            return Path.GetFullPath(configured);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new IOException("The local application-data directory is unavailable.");
        return Path.Combine(local, "CSweet", "Setup");
    }

    private static StoredProgress? ReadProgress(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            return JsonSerializer.Deserialize<StoredProgress>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteProgress(string path, StoredProgress progress)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        WriteProtectedFile(temporary, JsonSerializer.Serialize(progress));
        File.Move(temporary, path, true);
        RestrictFile(path);
    }

    private static string? ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var result = File.ReadAllText(path).Trim();
            return result is "completed" or "failed" ? result : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteProtectedFile(string path, string value)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(value);
        RestrictFile(path);
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsProcessRunning(int? processId)
    {
        if (processId is null) return false;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private StoredProgress Failed(StoredProgress progress, string code, string message) => progress with
    {
        State = "failed",
        PhaseKey = "installation-failed",
        PhaseDisplayName = "Execution-node installation paused",
        Message = message,
        UpdatedAt = timeProvider.GetUtcNow(),
        ErrorCode = code,
        ErrorMessage = message
    };

    private static LocalExecutionNodeProvisioningProgress Map(StoredProgress progress) => new(
        progress.JobId, progress.Platform, progress.State, progress.PhaseKey,
        progress.PhaseDisplayName, progress.Message, progress.PercentComplete,
        progress.StartedAt, progress.UpdatedAt, progress.RequiresRestart,
        progress.ErrorCode, progress.ErrorMessage, progress.OwnerProcessId, null, null);

    internal static LocalExecutionNodeProvisioningProgress? MapWindowsProgress(
        WindowsRuntimeHostProvisioningProgress? progress) => progress is null ? null : new(
        progress.JobId,
        "windows",
        progress.State.ToString().ToLowerInvariant(),
        progress.PhaseKey,
        progress.PhaseDisplayName,
        progress.Message,
        progress.PercentComplete,
        progress.StartedAt,
        progress.UpdatedAt,
        progress.RequiresRestart,
        progress.ErrorCode,
        progress.ErrorMessage,
        progress.OwnerProcessId,
        progress.EstimatedRemainingMinimumSeconds,
        progress.EstimatedRemainingMaximumSeconds);

    private static LocalExecutionNodeProvisioningResult Failure(string code, string message) =>
        new(false, code, message, false);

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record StoredProgress(
        Guid JobId,
        string Platform,
        string State,
        string PhaseKey,
        string PhaseDisplayName,
        string Message,
        int PercentComplete,
        DateTimeOffset StartedAt,
        DateTimeOffset UpdatedAt,
        bool RequiresRestart,
        string? ErrorCode,
        string? ErrorMessage,
        int? OwnerProcessId,
        string ResultPath);
}
