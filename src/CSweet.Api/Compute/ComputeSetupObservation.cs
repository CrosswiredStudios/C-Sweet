using System.Diagnostics;
using System.Text.Json;
using CSweet.Domain.Compute;

namespace CSweet.Api.Compute;

internal sealed record ComputeSetupObservation(bool? InstallerRunning, string? FailureCode)
{
    internal static ComputeSetupObservation Read(ComputeLocalSetup setup, string directory,
        Func<int, DateTimeOffset, bool?>? running = null)
    {
        if (setup.State != "Running") return new(null, null);
        try
        {
            var result = ReadJson(Path.Combine(directory, "result.json"));
            using (result)
            {
                if (Matches(result, setup) && result!.RootElement.GetProperty("state").GetString() == "Failed")
                    return new(false, "local_setup_failed");
            }
            using var process = ReadJson(Path.Combine(directory, "process.json"));
            if (Matches(process, setup))
            {
                var root = process!.RootElement;
                var alive = (running ?? IsRunning)(root.GetProperty("processId").GetInt32(), root.GetProperty("startedAt").GetDateTimeOffset());
                return new(alive, alive == false ? "setup_interrupted" : null);
            }
            // Older installers have no process/result receipt. A terminated transcript can still prove failure.
            var log = new FileInfo(Path.Combine(directory, "installation.log"));
            if (log.Exists && log.Length <= 1_048_576 && log.LastWriteTimeUtc >= setup.UpdatedAt.UtcDateTime)
            {
                using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (text.Contains("Windows PowerShell transcript end", StringComparison.Ordinal) &&
                    text.Contains("TerminatingError(Install-ComputeLocalProvider.ps1)", StringComparison.Ordinal))
                    return new(false, text.Contains("Error exporting vm", StringComparison.Ordinal) ? "image_export_failed" : "local_setup_failed");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        return new(null, null);
    }

    private static bool Matches(JsonDocument? document, ComputeLocalSetup setup) => document is not null &&
        setup.HandoffHash is not null && document.RootElement.TryGetProperty("attemptHash", out var hash) && hash.GetString() == setup.HandoffHash;

    private static JsonDocument? ReadJson(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length <= 16384 ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    }

    private static bool? IsRunning(int pid, DateTimeOffset started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (process.StartTime.ToUniversalTime() - started.UtcDateTime).Duration() < TimeSpan.FromSeconds(1);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
