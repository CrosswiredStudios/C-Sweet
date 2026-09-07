using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

internal static class AgentStartupRecovery
{
    internal static void ConfigurationChanged(AgentInstallation installation)
    {
        if (installation.Schedule is not { } schedule) return;
        schedule.ConsecutiveStartupFailures = 0;
        schedule.AutomaticStartSuppressedAt = null;
        if (installation.IsEnabled && schedule.IsEnabled && schedule.ActivationMode == ActivationMode.AlwaysOn)
            schedule.NextTickAt = DateTimeOffset.UtcNow;
    }
}
