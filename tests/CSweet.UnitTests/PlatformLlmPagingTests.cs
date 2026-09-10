using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class PlatformLlmPagingTests
{
    [Fact]
    public void AcknowledgedStreamCanExceedLifetimeLimitsWithoutLosingReplay()
    {
        var buffer = new PlatformLlmResultBuffer();
        var cursor = 0;
        var payload = JsonPayload.From(new byte[1024]);
        for (var batch = 0; batch < 800; batch++)
        {
            for (var i = 0; i < 64; i++)
                buffer.Add(new() { RequestId = "test", Succeeded = true, Sequence = batch * 64 + i, Payload = payload });
            var page = buffer.Read(cursor);
            Assert.Equal(64, page.Length);
            Assert.Equal(page, buffer.Read(cursor)); // Response loss must allow the same page to be read again.
            Assert.Equal(Enumerable.Range(cursor, page.Length), page.Select(x => x.Sequence));
            cursor += page.Length;
            Assert.InRange(buffer.Bytes, 0, 128 * 1024);
        }
        Assert.Equal(51200, cursor);
        Assert.Empty(buffer.Read(cursor));
        Assert.Equal(0, buffer.Bytes);
        Assert.Equal(0, buffer.Count);
        Assert.Throws<ArgumentException>(() => buffer.Read(cursor - 1));
    }

    [Fact]
    public void UnreadOutputRemainsBoundedAndCannotBeAcknowledgedWithoutDelivery()
    {
        var buffer = new PlatformLlmResultBuffer();
        var chunk = new CapabilityResult { RequestId = "test", Succeeded = true, Payload = JsonPayload.From(new byte[1024]) };
        for (var i = 0; i < 16384; i++) buffer.Add(chunk);
        Assert.Throws<ArgumentException>(() => buffer.Read(1));
        Assert.Throws<InvalidOperationException>(() => buffer.Add(chunk));
        var page = buffer.Read(0);
        Assert.Equal(64, page.Length);
        Assert.Equal(16 * 1024 * 1024, buffer.Bytes); // Reading alone does not acknowledge the page.
        buffer.Read(page.Length);
        buffer.Add(chunk);
        Assert.True(buffer.Bytes < 16 * 1024 * 1024);
    }

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
