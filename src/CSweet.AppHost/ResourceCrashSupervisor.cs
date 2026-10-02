using Aspire.Hosting;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Development crash supervision for long-running C-Sweet services. When a supervised process exits
/// with a non-zero code that nobody asked for (a crash, not a dashboard Stop), the supervisor writes a
/// crash report (exit code, recent console output, dump location) and restarts the resource with
/// bounded exponential backoff. Durable work leases recover on their own once the host is back, so a
/// crash costs one interrupted attempt instead of a stalled company.
/// </summary>
internal sealed class ResourceCrashSupervisor(
    ResourceNotificationService notifications,
    ResourceCommandService commands,
    ResourceLoggerService logs,
    CrashSupervisionOptions options,
    ILogger<ResourceCrashSupervisor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ResourceTracker> _trackers = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.CrashDirectory);
        await foreach (var change in notifications.WatchAsync(stoppingToken))
        {
            if (!options.Resources.Contains(change.Resource.Name)) continue;
            var tracker = _trackers.GetOrAdd(change.ResourceId, id =>
            {
                var created = new ResourceTracker(change.Resource.Name, options.LogLinesToKeep);
                _ = CaptureLogsAsync(id, created, stoppingToken);
                return created;
            });
            var state = change.Snapshot.State?.Text;
            var previous = tracker.LastState;
            tracker.LastState = state;
            if (state == previous) continue;

            if (Is(state, KnownResourceStates.Stopping))
            {
                tracker.StopRequested = true;
                continue;
            }
            if (Is(state, KnownResourceStates.Starting) || Is(state, KnownResourceStates.Running))
            {
                tracker.StopRequested = false;
                continue;
            }
            if (!Is(state, KnownResourceStates.Finished) && !Is(state, KnownResourceStates.Exited)) continue;

            var exitCode = change.Snapshot.ExitCode;
            if (tracker.StopRequested || exitCode is null or 0) continue;

            var restartAttempt = tracker.RecordCrash(DateTimeOffset.UtcNow, options.RestartWindow);
            var report = WriteCrashReport(change, tracker, exitCode.Value, restartAttempt);
            if (restartAttempt > options.MaximumRestartsPerWindow)
            {
                logger.LogError(
                    "{Resource} crashed with exit code {ExitCode} ({ExitCodeHex}); {Count} crashes within {Window}. " +
                    "Automatic restart is paused. Report: {Report}",
                    tracker.Name, exitCode, Hex(exitCode.Value), restartAttempt, options.RestartWindow, report);
                continue;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(
                options.MaximumRestartDelay.TotalSeconds,
                options.InitialRestartDelay.TotalSeconds * Math.Pow(2, restartAttempt - 1)));
            logger.LogError(
                "{Resource} crashed with exit code {ExitCode} ({ExitCodeHex}). Restart {Attempt}/{Maximum} in {Delay}. Report: {Report}",
                tracker.Name, exitCode, Hex(exitCode.Value), restartAttempt, options.MaximumRestartsPerWindow, delay, report);
            _ = RestartAfterAsync(change.ResourceId, tracker, delay, stoppingToken);
        }
    }

    private async Task RestartAfterAsync(string resourceId, ResourceTracker tracker, TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            // A manual start during the delay already recovered the resource.
            if (Is(tracker.LastState, KnownResourceStates.Starting) || Is(tracker.LastState, KnownResourceStates.Running)) return;
            var result = await commands.ExecuteCommandAsync(resourceId, KnownResourceCommands.StartCommand, token);
            if (!result.Success)
                logger.LogError("Automatic restart of {Resource} failed: {Error}", tracker.Name, result.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic restart of {Resource} failed.", tracker.Name);
        }
    }

    private async Task CaptureLogsAsync(string resourceId, ResourceTracker tracker, CancellationToken token)
    {
        try
        {
            await foreach (var batch in logs.WatchAsync(resourceId).WithCancellation(token))
                foreach (var line in batch)
                    tracker.AddLine(line.IsErrorMessage ? "stderr " + line.Content : line.Content);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Console capture for {Resource} stopped; crash reports will omit recent output.", tracker.Name);
        }
    }

    private string WriteCrashReport(ResourceEvent change, ResourceTracker tracker, int exitCode, int restartAttempt)
    {
        var now = DateTimeOffset.UtcNow;
        var path = Path.Combine(options.CrashDirectory,
            $"{tracker.Name}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.log");
        var report = new StringBuilder()
            .AppendLine($"Resource: {tracker.Name} ({change.ResourceId})")
            .AppendLine($"Crashed at (UTC): {now:O}")
            .AppendLine($"Started at (UTC): {change.Snapshot.StartTimeStamp:O}")
            .AppendLine($"Exit code: {exitCode} ({Hex(exitCode)}) {Describe(exitCode)}")
            .AppendLine($"Crash number within {options.RestartWindow}: {restartAttempt}")
            .AppendLine($"Crash dumps (if the runtime wrote one): {options.DumpDirectory}")
            .AppendLine("Windows also records the faulting module in Event Viewer > Windows Logs > Application")
            .AppendLine("(\"Application Error\" / \".NET Runtime\" entries at the crash time).")
            .AppendLine()
            .AppendLine($"Last {tracker.LineCount} console lines:")
            .AppendJoin(Environment.NewLine, tracker.Lines())
            .AppendLine();
        try
        {
            File.WriteAllText(path, report.ToString());
            PruneOldFiles(options.CrashDirectory, "*.log", options.ReportsToKeep);
            PruneOldFiles(options.DumpDirectory, "*.dmp", options.DumpsToKeep);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write crash report {Path}.", path);
        }
        return path;
    }

    private static void PruneOldFiles(string directory, string pattern, int keep)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var stale in new DirectoryInfo(directory).GetFiles(pattern)
                     .OrderByDescending(x => x.LastWriteTimeUtc).Skip(keep))
        {
            try { stale.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // KnownResourceStates are static readonly strings, not constants, so they cannot be pattern-matched.
    private static bool Is(string? state, string known) => string.Equals(state, known, StringComparison.Ordinal);

    internal static string Hex(int exitCode) => "0x" + unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture);

    internal static string Describe(int exitCode) => unchecked((uint)exitCode) switch
    {
        0xC0000005 => "access violation (native crash; see dump / Event Viewer for the faulting module)",
        0xC00000FD => "stack overflow",
        0xC0000409 => "fail-fast / stack buffer overrun (often Environment.FailFast or a CLR fatal error)",
        0xE0434352 => "unhandled .NET exception",
        0x80131506 => "CLR fatal execution engine error",
        _ => string.Empty
    };

    private sealed class ResourceTracker(string name, int capacity)
    {
        private readonly Queue<string> _lines = new();
        private readonly List<DateTimeOffset> _crashes = [];
        public string Name { get; } = name;
        public string? LastState { get; set; }
        public bool StopRequested { get; set; }
        public int LineCount { get { lock (_lines) return _lines.Count; } }

        public void AddLine(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > capacity) _lines.Dequeue();
            }
        }

        public IReadOnlyList<string> Lines() { lock (_lines) return _lines.ToArray(); }

        public int RecordCrash(DateTimeOffset now, TimeSpan window)
        {
            lock (_crashes)
            {
                _crashes.RemoveAll(x => now - x > window);
                _crashes.Add(now);
                return _crashes.Count;
            }
        }
    }
}

internal sealed record CrashSupervisionOptions(
    IReadOnlySet<string> Resources,
    string CrashDirectory,
    string DumpDirectory)
{
    public int LogLinesToKeep { get; init; } = 400;
    public int MaximumRestartsPerWindow { get; init; } = 5;
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumRestartDelay { get; init; } = TimeSpan.FromMinutes(2);
    public int ReportsToKeep { get; init; } = 50;
    public int DumpsToKeep { get; init; } = 3;
}

internal static class CrashSupervisionExtensions
{
    /// <summary>
    /// Asks the .NET runtime to write a minidump with heap when the process crashes, so native faults
    /// such as 0xC0000005 can be analysed (dotnet-dump analyze / WinDbg) instead of only seen as exit codes.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithCrashDumps(
        this IResourceBuilder<ProjectResource> resource, string dumpDirectory) =>
        resource
            .WithEnvironment("DOTNET_DbgEnableMiniDump", "1")
            .WithEnvironment("DOTNET_DbgMiniDumpType", "2")
            .WithEnvironment("DOTNET_DbgMiniDumpName",
                Path.Combine(dumpDirectory, $"{resource.Resource.Name}-%p-%t.dmp"))
            .WithEnvironment("DOTNET_CreateDumpDiagnostics", "1");
}
