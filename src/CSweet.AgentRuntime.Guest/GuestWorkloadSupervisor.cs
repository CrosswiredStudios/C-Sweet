using System.Diagnostics;

namespace CSweet.AgentRuntime.Guest;

public interface IGuestWorkloadSupervisor : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(IReadOnlyList<string> entrypoint, long maximumLogBytes, CancellationToken cancellationToken);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);
}

public sealed class GuestWorkloadSupervisor(GuestServiceOptions options) : IGuestWorkloadSupervisor
{
    private readonly string _artifactRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ArtifactRoot));
    private Process? _process;
    private Task[] _logDrains = [];
    private long _logBytes;

    public bool IsRunning => _process is { HasExited: false };

    public Task StartAsync(IReadOnlyList<string> entrypoint, long maximumLogBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is not null) throw new InvalidOperationException("The guest workload can only be started once.");
        if (entrypoint.Count is < 1 or > 64) throw new InvalidDataException("The workload entrypoint is invalid.");
        if (maximumLogBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumLogBytes));

        var executable = ResolveExecutable(entrypoint[0]);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _artifactRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        foreach (var argument in entrypoint.Skip(1))
        {
            if (argument.IndexOf('\0') >= 0 || argument.Length > 4096)
                throw new InvalidDataException("A workload argument is invalid.");
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["HOME"] = "/tmp/csweet-agent";
        start.Environment["CSWEET_BROKER_ONLY"] = "1";
        start.Environment["CSweet__Agent__McpEndpoint"] = "http://localhost/mcp";
        start.Environment["CSweet__Agent__McpUnixSocketPath"] = options.LocalBrokerSocketPath;
        if (options.WorkloadKind == 1)
        {
            WriteWorkloadToken();
            start.Environment["CSweet__Agent__InstallationId"] = options.InstallationId!.Value.ToString("D");
            start.Environment["CSweet__Agent__BusinessId"] = options.BusinessId!;
            start.Environment["CSweet__Agent__RuntimeInstanceId"] = options.WorkloadId.ToString("D");
            start.Environment["CSweet__Agent__TickId"] = options.TickId!.Value.ToString("D");
            start.Environment["CSweet__Agent__WorkloadTokenFile"] = options.WorkloadTokenPath;
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException("The guest workload did not start.");
        _logDrains =
        [
            DrainBoundedAsync(_process.StandardOutput.BaseStream, maximumLogBytes, cancellationToken),
            DrainBoundedAsync(_process.StandardError.BaseStream, maximumLogBytes, cancellationToken)
        ];
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("The guest workload has not started.");
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(_logDrains).WaitAsync(cancellationToken);
        return process.ExitCode;
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited) return;
        try { process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(gracePeriod <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : gracePeriod);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            await StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            _process.Dispose();
        }
        if (File.Exists(options.WorkloadTokenPath)) File.Delete(options.WorkloadTokenPath);
    }

    private string ResolveExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            throw new InvalidDataException("The workload executable is invalid.");
        var candidate = Path.GetFullPath(Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(_artifactRoot, value));
        var relative = Path.GetRelativePath(_artifactRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("The workload executable escapes the read-only artifact root.");
        if (!File.Exists(candidate)) throw new FileNotFoundException("The workload executable is missing.", candidate);
        return candidate;
    }

    private async Task DrainBoundedAsync(Stream input, long maximumLogBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            if (Interlocked.Add(ref _logBytes, read) <= maximumLogBytes) continue;
            var process = _process;
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            throw new InvalidDataException("The workload exceeded its bounded log volume.");
        }
    }

    private void WriteWorkloadToken()
    {
        var directory = Path.GetDirectoryName(options.WorkloadTokenPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(options.WorkloadTokenPath, options.BootToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(options.WorkloadTokenPath, UnixFileMode.UserRead);
    }
}
