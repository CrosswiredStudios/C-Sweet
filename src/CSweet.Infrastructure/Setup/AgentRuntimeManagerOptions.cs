namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeManagerOptions
{
    public const string SectionName = "CSweet:AgentRuntime";
    public const string DefaultMcpEndpoint = "http://agenthost:8081/mcp";
    public string McpEndpoint { get; set; } = DefaultMcpEndpoint;
    public string DockerNetworkName { get; set; } = "csweet-runtime";
    public string McpGatewayContainer { get; set; } = "agenthost";
    public bool CleanupContainersOnStartup { get; set; } = true;
    public bool SessionWatchdogEnabled { get; set; } = true;
    public int SessionWatchdogStartupGraceSeconds { get; set; } = 30;
    public int SessionWatchdogIntervalSeconds { get; set; } = 10;
    public int SessionDisconnectShutdownSeconds { get; set; } = 120;
    public string WorkloadSecretDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "csweet-runtime-secrets");
    public int MaximumScheduleClaimsPerIteration { get; set; } = 10;
    public int InteractiveIdleTimeoutSeconds { get; set; } = 300;
    /// <summary>
    /// Curated, vulnerability-scanned image for software-development-polyglot-v1.
    /// Production configuration must use an immutable digest reference.
    /// </summary>
    public string SoftwareDevelopmentPolyglotImage { get; set; } =
        "csweet/software-development-polyglot-v1:local";
    /// <summary>Controlled HTTP CONNECT proxy reachable from the isolated runtime network.</summary>
    public string? SoftwareDevelopmentEgressProxyUrl { get; set; }
}
