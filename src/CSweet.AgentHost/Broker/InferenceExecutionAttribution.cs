namespace CSweet.AgentHost.Broker;

/// <summary>Flows the already-authorized queue lease into the scoped provider handler.</summary>
internal sealed class InferenceExecutionAttribution(Guid workId, Guid queueJobId) : IDisposable
{
    private static readonly AsyncLocal<InferenceExecutionAttribution?> Slot = new();
    private readonly InferenceExecutionAttribution? previous = Slot.Value;
    public static InferenceExecutionAttribution? Current => Slot.Value;
    public Guid WorkId { get; } = workId;
    public Guid QueueJobId { get; } = queueJobId;
    public InferenceExecutionAttribution Enter() { Slot.Value = this; return this; }
    public void Dispose() => Slot.Value = previous;
}
