namespace CSweet.Api.Chat;

public sealed class ChatTurnOptions
{
    public TimeSpan HardTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan DirectFallbackTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MemoryOperationTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan StreamHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    // Interactive dispatch can be the action that starts an isolated agent workload. Wait
    // through that bounded startup window instead of treating Starting as failure.
    public TimeSpan RuntimeStartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
    // A runtime reported as ready should already have a broker session. Allow only a
    // short reconnect grace period before using the direct provider fallback; a long
    // wait here makes an otherwise healthy chat look hung when broker state is stale.
    public TimeSpan CapabilityRegistrationTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CapabilityRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
