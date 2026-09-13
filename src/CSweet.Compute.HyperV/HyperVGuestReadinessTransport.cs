using CSweet.Isolation.HyperV;

namespace CSweet.Compute.HyperV;

/// <summary>Provider-selected guest service; no agent-selected address, port or host path.</summary>
internal static class HyperVGuestReadinessTransport
{
    public const int GuestPort = CSweet.Compute.Contracts.ComputeGuestWire.Port;
    public static Task<Stream> ConnectAsync(Guid ownedVmId, CancellationToken token) =>
        new WindowsHyperVSocketTransport(new()
        {
            LinuxVsockPort = GuestPort,
            ConnectTimeoutSeconds = 3,
            RetryDelayMilliseconds = 100
        }).ConnectAsync(ownedVmId, token);
}
