using CSweet.Contracts.Setup;

namespace CSweet.UI.Services;

/// <summary>Tracks the initial Office connection window across layout changes.</summary>
public sealed class ExecutionCapacityAlertState
{
    private static readonly TimeSpan InitialConnectionGracePeriod = TimeSpan.FromMinutes(2);
    private DateTimeOffset? _firstCheckAt;
    private bool _initialConnectionFinished;

    public ExecutionCapacityAlertStatus Status { get; private set; }

    public void Update(ExecutionCapacityOnboardingResponse capacity, DateTimeOffset now)
    {
        _firstCheckAt ??= now;
        if (capacity.IsReady)
        {
            _initialConnectionFinished = true;
            Status = ExecutionCapacityAlertStatus.Hidden;
            return;
        }

        // Only an existing, approved Office can be reconnecting. Configuration and
        // enrollment problems need attention immediately, even during the first check.
        var canReconnect = capacity.Nodes.Any(node =>
            node.Status is "ready" or "offline" && node.CertificateExpiresAt > now);
        var policyReady = new[] { "launch-gate", "image-policy" }.All(key =>
            capacity.Checks.Any(check => check.Key == key && check.Status == "passed"));
        if (!_initialConnectionFinished && canReconnect && policyReady &&
            now - _firstCheckAt.Value < InitialConnectionGracePeriod)
        {
            Status = ExecutionCapacityAlertStatus.Connecting;
            return;
        }

        _initialConnectionFinished = true;
        Status = ExecutionCapacityAlertStatus.Unavailable;
    }

    // Unknown connectivity must not be reported as fleet loss, or restart the grace period.
    public void Hide() => Status = ExecutionCapacityAlertStatus.Hidden;
}

public enum ExecutionCapacityAlertStatus
{
    Hidden,
    Connecting,
    Unavailable
}
