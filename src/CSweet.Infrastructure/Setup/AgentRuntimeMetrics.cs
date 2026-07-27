using System.Diagnostics.Metrics;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

public static class AgentRuntimeMetrics
{
    public const string MeterName = "CSweet.AgentRuntime";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> ScheduleTicks = Meter.CreateCounter<long>("csweet.agent.schedule.ticks");
    private static readonly Counter<long> ContainerStarts = Meter.CreateCounter<long>("csweet.agent.container.starts");
    private static readonly Counter<long> ContainerStops = Meter.CreateCounter<long>("csweet.agent.container.stops");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("csweet.agent.runtime.outcomes");
    private static readonly Counter<long> CleanupItems = Meter.CreateCounter<long>("csweet.agent.cleanup.items");
    private static readonly Counter<long> Sessions = Meter.CreateCounter<long>("csweet.agent.mcp.sessions");
    private static readonly Counter<long> SessionDenials = Meter.CreateCounter<long>("csweet.agent.mcp.session.denials");
    private static readonly Counter<long> WorkEvents = Meter.CreateCounter<long>("csweet.agent.work.events");
    private static readonly Histogram<double> QueueLatency =
        Meter.CreateHistogram<double>("csweet.agent.work.queue_latency", "s");
    private static readonly Histogram<double> RuntimeDuration = Meter.CreateHistogram<double>("csweet.agent.runtime.duration", "s");

    public static void Tick(string activationMode, string outcome) =>
        ScheduleTicks.Add(1,
            new KeyValuePair<string, object?>("activation_mode", activationMode),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static void ContainerStarted() => ContainerStarts.Add(1);
    public static void ContainerStopped(string outcome) => ContainerStops.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RuntimeOutcome(AgentRuntimeStatus status, TimeSpan? duration)
    {
        Outcomes.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
        if (duration is { } value) RuntimeDuration.Record(value.TotalSeconds, new KeyValuePair<string, object?>("status", status.ToString()));
    }

    public static void Cleaned(string resource, int count)
    {
        if (count > 0) CleanupItems.Add(count, new KeyValuePair<string, object?>("resource", resource));
    }

    public static void Session(string operation) =>
        Sessions.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public static void SessionDenied(string reason) =>
        SessionDenials.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public static void Work(string operation, AgentWorkKind kind) =>
        WorkEvents.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("kind", kind.ToString()));

    public static void WorkClaimed(AgentWorkKind kind, TimeSpan queueLatency)
    {
        Work("claimed", kind);
        QueueLatency.Record(queueLatency.TotalSeconds,
            new KeyValuePair<string, object?>("kind", kind.ToString()));
    }
}
