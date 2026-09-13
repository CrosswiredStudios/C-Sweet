namespace CSweet.Compute.Contracts;

/// <summary>Exactly one guest operation. No host paths, addresses, or hypervisor selectors.</summary>
public sealed record ComputeWorkload(ComputeGuestCommand? Command = null, int? PublishPort = null)
{
    public ComputeWorkload Validate(string operatingSystem)
    {
        if ((Command is null) == (PublishPort is null)) throw new ArgumentException("Choose one workload operation.");
        if (PublishPort is { } port && port is < 1024 or > 65535) throw new ArgumentException("Invalid guest port.");
        var command = Command?.ValidateAndSnapshot(operatingSystem);
        // Fit signed provider result envelopes and the short dispatch window.
        if (command is not null && (command.TimeoutSeconds > 30 || command.MaximumOutputBytes > 8192))
            throw new ArgumentException("MVP commands allow 30 seconds and 8192 output bytes.");
        return this with { Command = command };
    }
}

public sealed record ComputeWorkloadResult(ComputeGuestWireResult? Command = null, string? Url = null,
    DateTimeOffset? UrlExpiresAt = null, string? ErrorCode = null);
