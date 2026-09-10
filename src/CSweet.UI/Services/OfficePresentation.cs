using CSweet.Contracts.Setup;

namespace CSweet.UI.Services;

public static class OfficePresentation
{
    public static bool IsConnected(ExecutionNodeSummaryResponse office, DateTimeOffset now) =>
        office.LastHeartbeatAt >= now.AddSeconds(-30) && office.CertificateExpiresAt > now;

    public static string Status(ExecutionNodeSummaryResponse office, DateTimeOffset now) => office.Status switch
    {
        "revoked" => "Revoked",
        "pendingapproval" => "Awaiting approval",
        "ready" or "draining" or "offline" when !IsConnected(office, now) => "Offline",
        "draining" => "Draining",
        "ready" => "Ready",
        _ => "Needs attention"
    };

    public static bool ReadyForUpgrade(ExecutionNodeSummaryResponse office, int? activeWork, DateTimeOffset now) =>
        office.Status == "draining" && IsConnected(office, now) && activeWork == 0;
}
