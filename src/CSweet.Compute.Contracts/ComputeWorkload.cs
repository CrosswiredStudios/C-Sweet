namespace CSweet.Compute.Contracts;

/// <summary>Exactly one guest operation. No host paths, addresses, or hypervisor selectors.</summary>
public sealed record ComputeWorkload(ComputeGuestCommand? Command = null, int? PublishPort = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ComputeNodeDependencies? NodeDependencies = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ComputeBrowserTest? BrowserTest = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ComputeEvidenceArchive? EvidenceArchive = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ComputeGuestCommand? EffectiveCommand => BrowserTest?.Command() ?? Command;
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> RequiredActions => PublishPort.HasValue
        ? [InfrastructureActions.PublishPort, InfrastructureActions.Inbound]
        : NodeDependencies is null ? [InfrastructureActions.Execute] : [InfrastructureActions.Execute, InfrastructureActions.Outbound];
    public ComputeWorkload Validate(string operatingSystem)
    {
        if ((Command is null ? 0 : 1) + (PublishPort is null ? 0 : 1) + (BrowserTest is null ? 0 : 1) + (EvidenceArchive is null ? 0 : 1) != 1)
            throw new ArgumentException("Choose one workload operation.");
        var browser = BrowserTest?.Validate();
        var evidence = EvidenceArchive?.Validate();
        if (evidence is not null && (operatingSystem != "linux" || NodeDependencies is not null))
            throw new ArgumentException("Evidence archives require a Linux guest and no dependency restore.");
        if (browser is not null && (operatingSystem != "linux" || NodeDependencies is not null))
            throw new ArgumentException("Browser tests require a prepared Linux guest and no dependency restore.");
        if (PublishPort is { } port && port is < 1024 or > 65535) throw new ArgumentException("Invalid guest port.");
        var command = Command?.ValidateAndSnapshot(operatingSystem);
        var dependencies = NodeDependencies?.Validate();
        if (dependencies is not null && (command is null || operatingSystem != "linux"))
            throw new ArgumentException("Managed npm restore requires a Linux guest command.");
        // Fit signed provider result envelopes and the short dispatch window.
        if (command is not null && (command.TimeoutSeconds > 30 || command.MaximumOutputBytes > 8192))
            throw new ArgumentException("MVP commands allow 30 seconds and 8192 output bytes.");
        return this with { Command = command, NodeDependencies = dependencies, BrowserTest = browser, EvidenceArchive = evidence };
    }
}

public sealed record ComputeWorkloadResult(ComputeGuestWireResult? Command = null, string? Url = null,
    DateTimeOffset? UrlExpiresAt = null, string? ErrorCode = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ComputeEvidenceArtifact? Artifact = null);
