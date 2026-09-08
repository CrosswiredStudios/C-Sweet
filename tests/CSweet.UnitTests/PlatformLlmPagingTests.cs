using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class PlatformLlmPagingTests
{
    [Fact]
    public void LongTokenStreamDrainsWithoutLossAndWithinPageBudgets()
    {
        var chunks = Enumerable.Range(0, 17699).Select(i => new CapabilityResult
        {
            RequestId = "test", Succeeded = true, Sequence = i,
            Payload = JsonPayload.From(new byte[100])
        }).ToArray();
        var cursor = 0;
        var pages = 0;
        while (cursor < chunks.Length)
        {
            var page = PlatformLlmJobService.SelectResultPage(chunks, cursor);
            Assert.InRange(page.Length, 1, 256);
            Assert.True(page.Sum(x => x.Payload.Length) <= 64 * 1024);
            Assert.Equal(chunks.Skip(cursor).Take(page.Length), page);
            cursor += page.Length;
            pages++;
        }
        Assert.InRange(pages, 1, 70);
        Assert.Empty(PlatformLlmJobService.SelectResultPage(chunks, cursor));
    }

    [Fact]
    public void LargeChunksSplitByBytesAndRetainAnOversizedIndividualChunk()
    {
        var chunks = new[] { 40000, 40000, 70000 }.Select((length, i) => new CapabilityResult
        { RequestId = "test", Succeeded = true, Sequence = i, Payload = JsonPayload.From(new byte[length]) }).ToArray();
        for (var cursor = 0; cursor < chunks.Length; cursor++)
            Assert.Same(chunks[cursor], Assert.Single(PlatformLlmJobService.SelectResultPage(chunks, cursor)));
    }
}
