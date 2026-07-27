using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Application.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class DockerAgentContainerRunner(
    IDockerCommandExecutor docker,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<DockerAgentContainerRunner> logger) : IPluginContainerRunner
{
    private const int RuntimeUserId = 1654;
    private const string ManagedLabel = "com.csweet.agent-runtime=true";
    private static readonly Regex ManagedContainerName = new(
        @"^csweet-agent-(?<runtime>[0-9a-f]{32})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const string SessionWatchdogScript = """
        agent_assembly="$1"
        dotnet "$agent_assembly" &
        agent_pid=$!
        watchdog_pid=

        stop_children() {
          if [ -n "$watchdog_pid" ]; then kill "$watchdog_pid" 2>/dev/null || true; fi
          kill -TERM "$agent_pid" 2>/dev/null || true
        }
        trap stop_children TERM INT

        (
          sleep "$CSWEET_SESSION_WATCHDOG_STARTUP_GRACE_SECONDS"
          failed_seconds=0
          while kill -0 "$agent_pid" 2>/dev/null; do
            if bash -c ': > "/dev/tcp/$1/$2"' -- "$CSWEET_MCP_HOST" "$CSWEET_MCP_PORT" 2>/dev/null; then
              failed_seconds=0
            else
              failed_seconds=$((failed_seconds + CSWEET_SESSION_WATCHDOG_INTERVAL_SECONDS))
              if [ "$failed_seconds" -ge "$CSWEET_SESSION_DISCONNECT_SHUTDOWN_SECONDS" ]; then
                echo "C-Sweet MCP session watchdog: gateway unreachable for ${failed_seconds}s; stopping agent." >&2
                kill -TERM "$agent_pid" 2>/dev/null || true
                sleep 5
                kill -KILL "$agent_pid" 2>/dev/null || true
                exit 0
              fi
            fi
            sleep "$CSWEET_SESSION_WATCHDOG_INTERVAL_SECONDS"
          done
        ) &
        watchdog_pid=$!

        wait "$agent_pid"
        exit_code=$?
        kill "$watchdog_pid" 2>/dev/null || true
        wait "$watchdog_pid" 2>/dev/null || true
        exit "$exit_code"
        """;

    public DockerAgentContainerRunner(
        IDockerCommandExecutor docker,
        ILogger<DockerAgentContainerRunner> logger)
        : this(docker, Options.Create(new AgentRuntimeManagerOptions()), logger)
    {
    }

    public async Task<AgentContainerStatus> StartAsync(
        AgentContainerStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateStartRequest(request);
        if (!Uri.TryCreate(request.McpEndpoint, UriKind.Absolute, out var mcpUri) ||
            mcpUri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(mcpUri.Host))
        {
            throw new AgentContainerException("The broker endpoint must be an absolute HTTP or HTTPS URI.");
        }
        var runtimeOptions = options.Value;
        if (runtimeOptions.SessionWatchdogEnabled) ValidateWatchdogOptions(runtimeOptions);
        await EnsureNetworkAsync(request.NetworkName, cancellationToken);
        await ConnectMcpGatewayAsync(request, cancellationToken);
        var workloadSecretPath = await WriteWorkloadSecretAsync(
            runtimeOptions.WorkloadSecretDirectory,
            request.RuntimeInstanceId,
            request.WorkloadToken,
            cancellationToken);
        var cpus = (request.CpuPercent / 100m).ToString("0.##", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "run", "--detach", "--init",
            "--name", request.ContainerName,
            "--label", ManagedLabel,
            "--label", $"com.csweet.runtime-instance-id={request.RuntimeInstanceId:N}",
            "--label", $"com.csweet.installation-id={request.InstallationId:N}",
            "--network", request.NetworkName,
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges=true",
            "--user", $"{RuntimeUserId}:{RuntimeUserId}",
            "--memory", $"{request.MemoryMb}m",
            "--cpus", cpus,
            "--pids-limit", request.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--tmpfs", $"/tmp:rw,nosuid,nodev,noexec,size=64m,uid={RuntimeUserId},gid={RuntimeUserId}",
            "--mount", CreatePackageMount(request.PackagePath),
            "--mount", $"type=bind,source={workloadSecretPath},target=/run/secrets/csweet-workload-token,readonly",
            "--env", $"CSweet__Agent__RuntimeInstanceId={request.RuntimeInstanceId:D}",
            "--env", $"CSweet__Agent__TickId={request.TickId:D}",
            "--env", $"CSweet__Agent__InstallationId={request.InstallationId:D}",
            "--env", $"CSweet__Agent__BusinessId={request.BusinessId}",
            "--env", $"CSweet__Agent__McpEndpoint={request.McpEndpoint}",
            "--env", "CSweet__Agent__WorkloadTokenFile=/run/secrets/csweet-workload-token",
            "--env", $"CSweet__Agent__ManifestPath={request.ManifestPath}",
            "--env", "DOTNET_CLI_HOME=/tmp/dotnet",
            "--env", "DOTNET_NOLOGO=1",
        };

        if (runtimeOptions.SessionWatchdogEnabled)
        {
            args.AddRange([
                "--env", $"CSWEET_MCP_HOST={mcpUri.Host}",
                "--env", $"CSWEET_MCP_PORT={mcpUri.Port}",
                "--env", $"CSWEET_SESSION_WATCHDOG_STARTUP_GRACE_SECONDS={runtimeOptions.SessionWatchdogStartupGraceSeconds}",
                "--env", $"CSWEET_SESSION_WATCHDOG_INTERVAL_SECONDS={runtimeOptions.SessionWatchdogIntervalSeconds}",
                "--env", $"CSWEET_SESSION_DISCONNECT_SHUTDOWN_SECONDS={runtimeOptions.SessionDisconnectShutdownSeconds}"
            ]);
        }

        args.Add(request.RuntimeImage);
        if (runtimeOptions.SessionWatchdogEnabled)
        {
            // Raw string literals retain the source file's line endings. Normalize before
            // passing the script to a Linux container so a Windows checkout cannot inject
            // carriage returns into shell commands or the agent assembly argument.
            args.AddRange(["/bin/bash", "-c", NormalizeShellScript(SessionWatchdogScript), "--", $"/app/{request.EntryAssembly}"]);
        }
        else
        {
            args.AddRange(["dotnet", $"/app/{request.EntryAssembly}"]);
        }

        if (!string.IsNullOrWhiteSpace(request.PersistentDataVolumeName))
        {
            ValidateVolumeName(request.PersistentDataVolumeName);
            var imageIndex = args.IndexOf(request.RuntimeImage);
            args.InsertRange(imageIndex, ["--mount", $"type=volume,source={request.PersistentDataVolumeName},target=/data"]);
        }

        logger.LogInformation(
            "Starting agent container {ContainerName} for runtime {RuntimeInstanceId}, installation {InstallationId}, image {RuntimeImage}, network {NetworkName}, and MCP endpoint {McpEndpoint}",
            request.ContainerName,
            request.RuntimeInstanceId,
            request.InstallationId,
            request.RuntimeImage,
            request.NetworkName,
            request.McpEndpoint);
        var result = await docker.ExecuteAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            logger.LogError("Agent container {ContainerName} failed to start: {DockerError}", request.ContainerName, result.StandardError.Trim());
            throw new AgentContainerException($"Docker failed to start agent container: {SanitizeError(result.StandardError)}");
        }

        var id = result.StandardOutput.Trim();
        logger.LogInformation("Started agent container {ContainerId} for runtime {RuntimeInstanceId}", id, request.RuntimeInstanceId);
        return await InspectAsync(id, cancellationToken)
            ?? new AgentContainerStatus(id, request.ContainerName, AgentContainerState.Created, null, null, null, null);
    }

    public async Task StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        var seconds = Math.Max(0, (int)Math.Ceiling(gracePeriod.TotalSeconds));
        logger.LogInformation("Stopping agent container {ContainerId} with {GraceSeconds}s grace", containerId, seconds);
        await ExecuteRequiredAsync(["stop", "--time", seconds.ToString(CultureInfo.InvariantCulture), containerId], "stop", cancellationToken);
    }

    public async Task<AgentContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        var result = await docker.ExecuteAsync(["inspect", "--format", "{{json .}}", containerId], cancellationToken);
        if (result.ExitCode != 0)
        {
            return result.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase) ? null
                : throw new AgentContainerException($"Docker failed to inspect agent container: {SanitizeError(result.StandardError)}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var state = root.GetProperty("State");
        return new AgentContainerStatus(
            root.GetProperty("Id").GetString() ?? containerId,
            (root.GetProperty("Name").GetString() ?? string.Empty).TrimStart('/'),
            ParseState(state.GetProperty("Status").GetString()),
            state.TryGetProperty("ExitCode", out var exitCode) ? exitCode.GetInt32() : null,
            ParseTimestamp(state, "StartedAt"),
            ParseTimestamp(state, "FinishedAt"),
            state.TryGetProperty("Error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()) ? error.GetString() : null);
    }

    public async Task<IReadOnlyList<AgentManagedContainer>> ListManagedAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await docker.ExecuteAsync(
            ["ps", "--all", "--filter", "name=csweet-agent-", "--format", "{{.ID}}\t{{.Names}}"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new AgentContainerException(
                $"Docker failed to list managed agent containers: {SanitizeError(result.StandardError)}");
        }

        var managed = new List<AgentManagedContainer>();
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t', 2, StringSplitOptions.TrimEntries);
            if (fields.Length != 2) continue;
            var match = ManagedContainerName.Match(fields[1]);
            if (!match.Success || !Guid.TryParseExact(match.Groups["runtime"].Value, "N", out var runtimeId)) continue;
            managed.Add(new AgentManagedContainer(fields[0], fields[1], runtimeId));
        }
        return managed;
    }

    public Task RemoveAsync(string containerId, bool force = false, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        IReadOnlyList<string> args = force ? ["rm", "--force", containerId] : ["rm", containerId];
        logger.LogInformation("Removing agent container {ContainerId}; force={Force}", containerId, force);
        return ExecuteRequiredAsync(args, "remove", cancellationToken);
    }

    public async Task RemoveNetworkAsync(
        string networkName,
        string mcpGatewayContainer,
        CancellationToken cancellationToken = default)
    {
        ValidateNetworkName(networkName);
        ValidateContainerId(mcpGatewayContainer);

        var inspect = await docker.ExecuteAsync(["network", "inspect", networkName], cancellationToken);
        if (inspect.ExitCode != 0)
        {
            if (IsMissingNetworkError(inspect.StandardError)) return;
            throw new AgentContainerException(
                $"Docker failed to inspect agent runtime network '{networkName}' during cleanup: {SanitizeError(inspect.StandardError)}");
        }

        var disconnect = await docker.ExecuteAsync(
            ["network", "disconnect", "--force", networkName, mcpGatewayContainer],
            cancellationToken);
        if (disconnect.ExitCode != 0 &&
            !disconnect.StandardError.Contains("is not connected", StringComparison.OrdinalIgnoreCase) &&
            !disconnect.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase) &&
            !IsMissingNetworkError(disconnect.StandardError))
        {
            throw new AgentContainerException(
                $"Docker could not detach the broker gateway from runtime network '{networkName}': {SanitizeError(disconnect.StandardError)}");
        }

        var remove = await docker.ExecuteAsync(["network", "rm", networkName], cancellationToken);
        if (remove.ExitCode != 0 && !IsMissingNetworkError(remove.StandardError))
        {
            throw new AgentContainerException(
                $"Docker failed to remove agent runtime network '{networkName}': {SanitizeError(remove.StandardError)}");
        }

        logger.LogInformation("Removed isolated agent runtime network {NetworkName}.", networkName);
        DeleteWorkloadSecret(options.Value.WorkloadSecretDirectory, networkName);
    }

    public async Task<string> GetLogsAsync(string containerId, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var result = await docker.ExecuteAsync(["logs", containerId], cancellationToken);
        if (result.ExitCode != 0) throw new AgentContainerException($"Docker failed to read agent container logs: {SanitizeError(result.StandardError)}");
        var combined = result.StandardOutput + result.StandardError;
        var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        return bytes.Length <= maximumBytes
            ? combined
            : System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, maximumBytes));
    }

    private async Task ExecuteRequiredAsync(IReadOnlyList<string> arguments, string operation, CancellationToken cancellationToken)
    {
        var result = await docker.ExecuteAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            logger.LogError("Docker failed to {Operation} agent container: {DockerError}", operation, result.StandardError.Trim());
            throw new AgentContainerException($"Docker failed to {operation} agent container: {SanitizeError(result.StandardError)}");
        }
    }

    private async Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        ValidateNetworkName(networkName);
        var inspect = await docker.ExecuteAsync(["network", "inspect", networkName], cancellationToken);
        if (inspect.ExitCode == 0)
        {
            logger.LogDebug("Using existing Docker network {NetworkName} for agent runtimes.", networkName);
            return;
        }

        if (!IsMissingNetworkError(inspect.StandardError))
        {
            throw new AgentContainerException(
                $"Docker failed to inspect agent runtime network '{networkName}': {SanitizeError(inspect.StandardError)}");
        }

        logger.LogWarning("Docker network {NetworkName} was missing; creating a bridge network for agent runtimes.", networkName);
        var create = await docker.ExecuteAsync(["network", "create", "--driver", "bridge", "--internal", networkName], cancellationToken);
        if (create.ExitCode != 0 && !create.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentContainerException(
                $"Docker failed to create agent runtime network '{networkName}': {SanitizeError(create.StandardError)}");
        }
    }

    private async Task ConnectMcpGatewayAsync(AgentContainerStartRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.McpGatewayContainer) ||
            request.McpGatewayContainer.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new AgentContainerException("A valid broker gateway container is required for the isolated runtime network.");
        var mcpHost = new Uri(request.McpEndpoint).Host;
        var connect = await docker.ExecuteAsync([
            "network", "connect", "--alias", mcpHost, request.NetworkName, request.McpGatewayContainer
        ], cancellationToken);
        if (connect.ExitCode != 0 && !connect.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            throw new AgentContainerException($"Docker could not attach the broker gateway to the isolated runtime network: {SanitizeError(connect.StandardError)}");
    }

    private static void ValidateStartRequest(AgentContainerStartRequest request)
    {
        if (request.MemoryMb <= 0 || request.CpuPercent <= 0 || request.PidsLimit <= 0 || request.MaxRuntimeSeconds <= 0)
            throw new AgentContainerException("Container resource and runtime limits must be positive.");
        if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.BusinessId) || string.IsNullOrWhiteSpace(request.RuntimeImage) || string.IsNullOrWhiteSpace(request.NetworkName) || string.IsNullOrWhiteSpace(request.McpEndpoint) || string.IsNullOrWhiteSpace(request.WorkloadToken))
            throw new AgentContainerException("Runtime image, broker endpoint, workload token, and isolated network are required.");
        ValidateNetworkName(request.NetworkName);
        if (!Path.IsPathFullyQualified(request.PackagePath) || request.PackagePath.Contains(',', StringComparison.Ordinal))
            throw new AgentContainerException("The package path must be absolute and cannot contain Docker mount delimiters.");
        if (request.EntryAssembly != Path.GetFileName(request.EntryAssembly) || !request.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new AgentContainerException("The entry assembly must be a package-root .dll file name.");
        if (!request.ManifestPath.StartsWith("/app/", StringComparison.Ordinal) || request.ManifestPath.Contains("..", StringComparison.Ordinal))
            throw new AgentContainerException("The manifest path must be inside the read-only package mount.");
        if (request.ContainerName.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new AgentContainerException("The container name contains unsupported characters.");
    }

    private static void ValidateWatchdogOptions(AgentRuntimeManagerOptions runtimeOptions)
    {
        if (runtimeOptions.SessionWatchdogStartupGraceSeconds < 0 ||
            runtimeOptions.SessionWatchdogIntervalSeconds <= 0 ||
            runtimeOptions.SessionDisconnectShutdownSeconds < runtimeOptions.SessionWatchdogIntervalSeconds)
        {
            throw new AgentContainerException(
                "Session watchdog timing must use a non-negative startup grace, a positive interval, and a disconnect timeout at least as long as the interval.");
        }
    }

    private static void ValidateVolumeName(string volumeName)
    {
        if (volumeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new AgentContainerException("The plugin data volume name contains unsupported characters.");
    }

    private static void ValidateContainerId(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId) || containerId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new AgentContainerException("The container identifier is invalid.");
    }

    private static void ValidateNetworkName(string networkName)
    {
        if (string.IsNullOrWhiteSpace(networkName) ||
            networkName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new AgentContainerException("The runtime network name contains unsupported characters.");
    }

    private static bool IsMissingNetworkError(string error)
        => error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("No such network", StringComparison.OrdinalIgnoreCase);

    private static AgentContainerState ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        "created" => AgentContainerState.Created, "running" => AgentContainerState.Running,
        "exited" => AgentContainerState.Exited, "dead" => AgentContainerState.Dead,
        "paused" => AgentContainerState.Paused, "restarting" => AgentContainerState.Restarting,
        _ => AgentContainerState.Unknown
    };

    private static DateTimeOffset? ParseTimestamp(JsonElement state, string property)
        => state.TryGetProperty(property, out var value) && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && timestamp != default ? timestamp : null;

    private static string SanitizeError(string error)
        => string.IsNullOrWhiteSpace(error) ? "unknown Docker error" : error.Trim().Replace("\r", " ").Replace("\n", " ");

    private static string NormalizeShellScript(string script) =>
        script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string CreatePackageMount(string packagePath)
    {
        var volumeName = Environment.GetEnvironmentVariable("CSWEET_AGENT_PACKAGE_VOLUME");
        var packageRoot = Environment.GetEnvironmentVariable("CSWEET_AGENT_PACKAGE_CACHE");
        if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(packageRoot))
            return $"type=bind,source={packagePath},target=/app,readonly";
        if (volumeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new AgentContainerException("The agent package volume name is invalid.");
        var root = Path.GetFullPath(packageRoot);
        var path = Path.GetFullPath(packagePath);
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new AgentContainerException("The runtime package is outside the approved package cache.");
        return $"type=volume,source={volumeName},target=/app,volume-subpath={relative},readonly";
    }

    private static async Task<string> WriteWorkloadSecretAsync(
        string secretDirectory,
        Guid runtimeInstanceId,
        string token,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(secretDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{runtimeInstanceId:N}.token");
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(path, token, cancellationToken);
            return path;
        }

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporaryPath = Path.Combine(directory, $"{runtimeInstanceId:N}.{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(token), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        if (Chown(temporaryPath, RuntimeUserId, RuntimeUserId) != 0)
        {
            File.Delete(temporaryPath);
            throw new AgentContainerException(
                $"The workload secret could not be assigned to runtime user {RuntimeUserId}.");
        }
        File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead);
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    private static void DeleteWorkloadSecret(string secretDirectory, string networkName)
    {
        var suffix = networkName.Split('-').LastOrDefault();
        if (suffix?.Length != 32 || !Guid.TryParseExact(suffix, "N", out var runtimeId))
            return;
        var directory = Path.GetFullPath(secretDirectory);
        var path = Path.GetFullPath(Path.Combine(directory, $"{runtimeId:N}.token"));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;
        if (File.Exists(path))
            File.Delete(path);
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, int owner, int group);
}
