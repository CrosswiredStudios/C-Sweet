using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace CSweet.UnitTests;

public sealed class ReasoningContentChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_reasoning_survives_tool_and_final_answer_history(bool streaming)
    {
        var requests = new List<JsonElement>();
        using var transport = new HttpClient(new RecordingHandler(async request =>
        {
            requests.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            return Reply(streaming);
        }));
        var sdkClient = new OpenAI.Chat.ChatClient("deepseek-flash", new ApiKeyCredential("test"),
            new OpenAIClientOptions { Endpoint = new Uri("https://provider.invalid/v1/"),
                Transport = new HttpClientPipelineTransport(transport) });
        using var client = OpenAiCompatibleLlmProviderFactory.AdaptChatClient(sdkClient);
        var options = new ChatOptions { Instructions = "Keep the system instructions.",
            Tools = [AIFunctionFactory.Create(() => "ok", "inspect")] };
        List<ChatMessage> messages = [new(ChatRole.User, "Inspect the project.")];

        var response = streaming
            ? await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync()
            : await client.GetResponseAsync(messages, options);
        Assert.Equal("Check the files.", string.Concat(response.Messages.SelectMany(x => x.Contents).OfType<TextReasoningContent>().Select(x => x.Text)));
        messages.AddRange(response.Messages);
        messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call_1", "ok")]));
        // Final answers also need their reasoning preserved on later tool-enabled turns.
        messages.Add(new(ChatRole.Assistant, [new TextReasoningContent("All checks "),
            new TextReasoningContent("passed."), new TextContent("Done.")]));
        messages.Add(new(ChatRole.Assistant, "Earlier answer without thinking."));
        messages.Add(new(ChatRole.Assistant, [new TextReasoningContent(""), new TextContent("Empty thinking.")]));
        messages.Add(new(ChatRole.User, "Continue."));
        var originalMessages = JsonSerializer.Serialize(messages);

        if (streaming)
            await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync();
        else
            await client.GetResponseAsync(messages, options);

        var wire = requests[1].GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", wire[0].GetProperty("role").GetString());
        var assistants = wire.Where(x => x.GetProperty("role").GetString() == "assistant").ToArray();
        Assert.Equal("Check the files.", assistants[0].GetProperty("reasoning_content").GetString());
        Assert.Equal("call_1", assistants[0].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("All checks passed.", assistants[1].GetProperty("reasoning_content").GetString());
        Assert.False(assistants[2].TryGetProperty("reasoning_content", out _));
        Assert.Equal("", assistants[3].GetProperty("reasoning_content").GetString());
        Assert.Contains(wire, x => x.GetProperty("role").GetString() == "tool" && x.GetProperty("tool_call_id").GetString() == "call_1");
        Assert.Equal(originalMessages, JsonSerializer.Serialize(messages));
        Assert.Null(options.RawRepresentationFactory);
        Assert.True(requests[1].TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Concurrent_requests_keep_reasoning_scoped_to_their_own_history()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var transport = new HttpClient(new RecordingHandler(async request =>
        {
            if (Interlocked.Increment(ref count) == 2) entered.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            var messages = body.GetProperty("messages");
            Assert.Equal(messages[0].GetProperty("content").GetString(),
                messages[1].GetProperty("reasoning_content").GetString());
            return Reply(false);
        }));
        var sdkClient = new OpenAI.Chat.ChatClient("test", new ApiKeyCredential("test"),
            new OpenAIClientOptions { Endpoint = new Uri("https://provider.invalid/v1/"),
                Transport = new HttpClientPipelineTransport(transport) });
        using var client = OpenAiCompatibleLlmProviderFactory.AdaptChatClient(sdkClient);
        Task<ChatResponse> Run(string text) => client.GetResponseAsync([
            new(ChatRole.User, text), new(ChatRole.Assistant, [new TextReasoningContent(text), new TextContent("Answer")])]);
        await Task.WhenAll(Run("conversation one"), Run("conversation two"));
    }

    private static HttpResponseMessage Reply(bool streaming)
    {
        const string completion = """
            {"id":"chat_1","object":"chat.completion","created":1,"model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":null,"reasoning_content":"Check the files.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
            """;
        const string events = """
            data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"deepseek-flash","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Check the "}}]}

            data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"deepseek-flash","choices":[{"index":0,"delta":{"reasoning_content":"files.","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]


            """;
        return new(HttpStatusCode.OK) { Content = new StringContent(streaming ? events : completion,
            Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
