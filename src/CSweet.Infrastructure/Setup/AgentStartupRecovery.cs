using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

internal static class AgentStartupRecovery
{
    internal static bool IsTransient(AgentRuntimeInstance runtime)
    {
        if (runtime.McpSessionEstablishedAt is not null || runtime.Status is not (
            AgentRuntimeStatus.StartFailed or AgentRuntimeStatus.Failed or
            AgentRuntimeStatus.ExitedWithoutCompletion or AgentRuntimeStatus.McpSessionTimedOut)) return false;
        if (runtime.Status == AgentRuntimeStatus.McpSessionTimedOut ||
            runtime.Events.Any(e => e.Reason?.Contains("MCP session establishment timed out", StringComparison.OrdinalIgnoreCase) == true)) return true;
        var diagnostics = (runtime.Reason ?? "") + "\n" + (runtime.LogExcerpt ?? "");
        return new[] { "502 (Bad Gateway)", "503 (Service Unavailable)", "BadGateway", "ServiceUnavailable", "connection refused", "SocketException", "HttpRequestException" }
            .Any(marker => diagnostics.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static TimeSpan RetryDelay(int failures, AgentRuntimeManagerOptions options)
    {
        var initial = Math.Clamp(options.StartupRetryInitialDelaySeconds, 1, 86400);
        var maximum = Math.Clamp(options.StartupRetryMaximumDelaySeconds, initial, 86400);
        return TimeSpan.FromSeconds(Math.Min(maximum, initial * Math.Pow(2, Math.Clamp(failures - 1, 0, 30))));
    }

    internal static void ConfigurationChanged(AgentInstallation installation)
    {
        if (installation.Schedule is not { } schedule) return;
        schedule.ConsecutiveStartupFailures = 0;
        schedule.AutomaticStartSuppressedAt = null;
        if (installation.IsEnabled && schedule.IsEnabled && schedule.ActivationMode == ActivationMode.AlwaysOn)
            schedule.NextTickAt = DateTimeOffset.UtcNow;
    }
}
