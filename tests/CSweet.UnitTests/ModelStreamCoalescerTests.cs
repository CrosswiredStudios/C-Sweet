using System.Runtime.CompilerServices;
using CSweet.AgentHost.Broker;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class ModelStreamCoalescerTests
{
    [Fact]
    public async Task Message_boundaries_and_response_metadata_survive_coalescing()
    {
        var source = new[] { "first", "second", "third", "fourth", "fifth" }.Select(Text).ToArray();
        foreach (var update in source) { update.ResponseId = "response"; update.MessageId = "a"; update.ModelId = "model"; }
        source[3].MessageId = source[4].MessageId = "b";
        var result = new List<ChatResponseUpdate>();
        await foreach (var update in ModelStreamCoalescer.ReadAsync(Stream(source), default)) result.Add(update);
        Assert.Equal(new[] { "first", "secondthird", "fourthfifth" }, result.Select(x => x.Text));
        Assert.Equal(new[] { "a", "a", "b" }, result.Select(x => x.MessageId));
        Assert.All(result, x => { Assert.Equal("response", x.ResponseId); Assert.Equal("model", x.ModelId); });
    }

    [Fact]
    public async Task Text_is_lossless_with_immediate_first_chunk_and_preserved_nontext_boundaries()
    {
        var reasoning = new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("reasoning")]);
        var tool = new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call", "validate")]);
        var usage = new ChatResponseUpdate(null, [new UsageContent(new() { OutputTokenCount = 80 })]);
        var terminal = new ChatResponseUpdate(ChatRole.Assistant, []) { FinishReason = ChatFinishReason.Stop };
        var source = Enumerable.Range(0, 80).Select(i => Text($"{i},")).ToList();
        source.Insert(41, reasoning); source.Add(tool); source.Add(usage); source.Add(terminal);
        var result = new List<ChatResponseUpdate>();
        await foreach (var update in ModelStreamCoalescer.ReadAsync(Stream(source), default)) result.Add(update);
        Assert.Same(source[0], result[0]); Assert.True(result.Count < 15);
        Assert.Equal(string.Concat(Enumerable.Range(0, 80).Select(i => $"{i},")), string.Concat(result.Select(x => x.Text)));
        Assert.Equal(new[] { reasoning, tool, usage, terminal }, result.Where(x => x.Contents.Count == 0 || x.Contents[0] is not TextContent));
        Assert.Equal("40,", result[result.IndexOf(reasoning) - 1].Text[^3..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buffered_text_is_emitted_before_provider_failure_or_cancellation(bool cancel)
    {
        var emitted = new List<ChatResponseUpdate>();
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var update in ModelStreamCoalescer.ReadAsync(Failing(cancel), default)) emitted.Add(update);
        });
        if (cancel) Assert.IsType<OperationCanceledException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("firstbuffered", string.Concat(emitted.Select(x => x.Text)));
    }

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);
    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        foreach (var update in updates) { token.ThrowIfCancellationRequested(); yield return update; }
        await Task.CompletedTask;
    }
    private static async IAsyncEnumerable<ChatResponseUpdate> Failing(bool cancel)
    {
        yield return Text("first"); yield return Text("buffered"); await Task.CompletedTask;
        if (cancel) throw new OperationCanceledException();
        throw new InvalidOperationException("Provider failed");
    }
}
