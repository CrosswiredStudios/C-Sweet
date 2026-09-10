using CSweet.Agent.SDK;

namespace CSweet.AgentHost.Broker;

/// <summary>Retains unacknowledged inference output, including the last page for replay.</summary>
internal sealed class PlatformLlmResultBuffer
{
    private readonly List<CapabilityResult> results = [];
    private int start;
    private int deliveredThrough;
    internal int Bytes { get; private set; }
    internal int Count => results.Count;
    internal int End => start + results.Count;

    // The caller holds the job lock for both adding and reading results.
    internal void Add(CapabilityResult result)
    {
        if (Bytes + result.Payload.Length > 16 * 1024 * 1024 || results.Count >= 32768)
            throw new InvalidOperationException("The inference result exceeded its bounded buffer.");
        results.Add(result);
        Bytes += result.Payload.Length;
    }

    internal CapabilityResult[] Read(int after)
    {
        if (after < start || after > deliveredThrough)
            throw new ArgumentException("Invalid or expired inference cursor.");
        var acknowledged = after - start;
        for (var i = 0; i < acknowledged; i++) Bytes -= results[i].Payload.Length;
        results.RemoveRange(0, acknowledged);
        start = after;
        var page = PlatformLlmJobService.SelectResultPage(results, 0);
        deliveredThrough = Math.Max(deliveredThrough, after + page.Length);
        return page;
    }
}
