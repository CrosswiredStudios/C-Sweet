using System.Text.Json;

namespace CSweet.AgentRuntime.HyperV;

internal static class WindowsRuntimeHostProgressStore
{
    internal const string ProgressRootEnvironmentVariable = "CSWEET_WINDOWS_ISOLATION_PROGRESS_ROOT";
    private const int MaximumProgressBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string CreatePath(Guid jobId) =>
        Path.Combine(ResolveRoot(), $"windows-isolation-{jobId:N}.json");

    public static WindowsRuntimeHostProvisioningProgress? ReadLatest(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var preferred = TryRead(preferredPath);
            if (preferred is not null) return preferred;
        }

        var root = ResolveRoot();
        try
        {
            if (!Directory.Exists(root)) return null;
            var latest = Directory.EnumerateFiles(root, "windows-isolation-*.json", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(20)
                .Select(info => TryRead(info.FullName))
                .FirstOrDefault(progress => progress is not null);

            // A preferred path belongs to the setup job launched by this app process, so its
            // terminal result remains useful to the UI. Without a preferred path, however, this
            // is a new app process. Only adopt an in-flight job that may still be running in the
            // elevated setup process. Replaying an old failure (or completion) makes a fresh
            // onboarding session look as though it attempted work that it never started.
            return CanResumeAcrossApplicationRestart(latest) ? latest : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static bool CanResumeAcrossApplicationRestart(WindowsRuntimeHostProvisioningProgress? progress) =>
        progress is { State: WindowsRuntimeHostProvisioningState.Running };

    internal static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(ProgressRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
            return Path.GetFullPath(configured);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(commonData, "CSweet", "Setup");
    }

    private static WindowsRuntimeHostProvisioningProgress? TryRead(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var fullPath = Path.GetFullPath(path);
            var rootPrefix = ResolveRoot().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length is < 32 or > MaximumProgressBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            var document = JsonSerializer.Deserialize<ProgressDocument>(File.ReadAllText(fullPath), JsonOptions);
            if (document is null || document.SchemaVersion != 1 || document.JobId == Guid.Empty ||
                document.PercentComplete is < 0 or > 100 ||
                document.StartedAt == default || document.UpdatedAt < document.StartedAt ||
                document.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
                document.OwnerProcessId is <= 0 ||
                string.IsNullOrWhiteSpace(document.PhaseKey) || document.PhaseKey.Length > 64 ||
                string.IsNullOrWhiteSpace(document.PhaseDisplayName) || document.PhaseDisplayName.Length > 160 ||
                document.Message?.Length > 512 || document.ErrorMessage?.Length > 1024 ||
                !TryState(document.State, out var state))
                return null;
            return new WindowsRuntimeHostProvisioningProgress(
                document.JobId,
                Bounded(document.Workflow, 64, "windows-isolation"),
                state,
                document.PhaseKey,
                document.PhaseDisplayName,
                Bounded(document.Message, 512, document.PhaseDisplayName),
                document.PercentComplete,
                document.StartedAt,
                document.UpdatedAt,
                BoundedSeconds(document.EstimatedRemainingMinimumSeconds),
                BoundedSeconds(document.EstimatedRemainingMaximumSeconds),
                document.RequiresRestart,
                NullIfEmpty(document.ErrorCode, 64),
                NullIfEmpty(document.ErrorMessage, 1024),
                document.OwnerProcessId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or JsonException)
        {
            return null;
        }
    }

    private static bool TryState(string? value, out WindowsRuntimeHostProvisioningState state)
    {
        state = value?.ToLowerInvariant() switch
        {
            "running" => WindowsRuntimeHostProvisioningState.Running,
            "restart-required" => WindowsRuntimeHostProvisioningState.RestartRequired,
            "completed" => WindowsRuntimeHostProvisioningState.Completed,
            "failed" => WindowsRuntimeHostProvisioningState.Failed,
            _ => (WindowsRuntimeHostProvisioningState)(-1)
        };
        return Enum.IsDefined(state);
    }

    private static int? BoundedSeconds(int? seconds) => seconds is >= 0 and <= 24 * 60 * 60 ? seconds : null;
    private static string Bounded(string? value, int maximum, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Length <= maximum ? value : value[..maximum];
    private static string? NullIfEmpty(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];

    private sealed class ProgressDocument
    {
        public int SchemaVersion { get; set; }
        public Guid JobId { get; set; }
        public string? Workflow { get; set; }
        public string? State { get; set; }
        public string PhaseKey { get; set; } = string.Empty;
        public string PhaseDisplayName { get; set; } = string.Empty;
        public string? Message { get; set; }
        public int PercentComplete { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int? OwnerProcessId { get; set; }
        public int? EstimatedRemainingMinimumSeconds { get; set; }
        public int? EstimatedRemainingMaximumSeconds { get; set; }
        public bool RequiresRestart { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
