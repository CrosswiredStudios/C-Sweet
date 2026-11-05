using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CSweet.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CSweet.UnitTests;

public sealed class ProviderOutputLimitChatClientTests
{
    private const string ObservedRejection = "request exceeds the context window: prompt (6060 tokens) + max tokens (128000) exceeds the context (131072); requests are never truncated. Send a smaller max_tokens (at most 125004 here), or start the server with --fit-max-tokens";
    private const string VllmRejection = "This model's maximum context length is 131072 tokens. However, you requested 128000 output tokens and your prompt contains 32000 input tokens, for a total of 160000 tokens (32000 + 128000 = 160000 > 131072). Please reduce the length of the input prompt or the number of requested output tokens.";
    private const string LegacyVllmRejection = "'max_tokens' or 'max_completion_tokens' is too large: 128000. This model's maximum context length is 131072 tokens and your request has 32000 input tokens (128000 > 131072 - 32000).";

    [Theory]
    [InlineData(false, true, ObservedRejection, 125004)]
    [InlineData(true, true, ObservedRejection, 125004)]
    [InlineData(true, false, ObservedRejection, 125004)]
    [InlineData(false, true, VllmRejection, 99072)]
    [InlineData(true, true, VllmRejection, 99072)]
    [InlineData(false, true, LegacyVllmRejection, 99072)]
    [InlineData(true, true, LegacyVllmRejection, 99072)]
    public async Task Rejected_ceiling_is_fitted_once_without_changing_input_or_saved_options(
        bool streaming, bool supportsStreaming, string rejection, int allowed)
    {
        var requests = new List<JsonElement>();
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            requests.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            return requests.Count == 1 ? Reject(rejection) : Reply(streaming && supportsStreaming);
        }));
        using var client = Create(http, supportsStreaming);
        var options = new ChatOptions { MaxOutputTokens = 128000, Temperature = 0.7f,
            Instructions = "Retain the approved task and all context.",
            Tools = [AIFunctionFactory.Create(() => "result", "inspect")],
            ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(new
                { type = "object", properties = new { answer = new { type = "string" } }, required = new[] { "answer" }, additionalProperties = false }), "answer") };
        ChatMessage[] history = [new(ChatRole.User, "The complete task"),
            new(ChatRole.Assistant, [new TextReasoningContent("Prior reasoning"), new TextContent("Prior answer")]),
            new(ChatRole.User, "Continue")];
        var original = JsonSerializer.Serialize(history);

        var response = await Run(client, history, options, streaming);

        Assert.Equal("Done", response.Text);
        Assert.Equal(2, requests.Count);
        Assert.Equal(128000, OutputLimit(requests[0]));
        Assert.Equal(allowed, OutputLimit(requests[1]));
        Assert.Equal(128000, options.MaxOutputTokens);
        Assert.Equal(original, JsonSerializer.Serialize(history));
        foreach (var property in new[] { "messages", "tools", "response_format", "temperature", "model", "stream" })
        {
            Assert.Equal(requests[0].TryGetProperty(property, out var before), requests[1].TryGetProperty(property, out var after));
            if (before.ValueKind != JsonValueKind.Undefined) Assert.Equal(before.GetRawText(), after.GetRawText());
        }
        Assert.Equal("Prior reasoning", requests[1].GetProperty("messages")[2].GetProperty("reasoning_content").GetString());
        Assert.Equal(15000, response.Usage!.OutputTokenCount);
        Assert.Equal(47000, response.Usage.TotalTokenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_request_keeps_the_full_ceiling_and_does_not_retry(bool streaming)
    {
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            requests++;
            Assert.Equal(128000, OutputLimit(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync())));
            return Reply(streaming);
        }));
        using var client = Create(http);
        var response = await Run(client, [new(ChatRole.User, "task")], new() { MaxOutputTokens = 128000 }, streaming);
        Assert.Equal(1, requests);
        Assert.Equal(15000, response.Usage!.OutputTokenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_rejection_propagates_without_a_retry_loop(bool streaming)
    {
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            requests++;
            return Task.FromResult(Reject(ObservedRejection));
        }));
        using var client = Create(http);
        await Assert.ThrowsAsync<ClientResultException>(() => Run(client, [new(ChatRole.User, "task")], new() { MaxOutputTokens = 128000 }, streaming));
        Assert.Equal(2, requests);
    }

    public static IEnumerable<object[]> UnrecoverableRejections()
    {
        string[] messages = [
            "request exceeds the context window",
            "This model's maximum context length is 131072 tokens. However, your request has 140000 input tokens. Please reduce the length of the input messages.",
            ObservedRejection.Replace("125004 here", "0 here"),
            ObservedRejection.Replace("125004 here", "999999 here"),
            ObservedRejection.Replace("6060 tokens", "140000 tokens"),
            ObservedRejection.Replace("6060 tokens", "1 tokens"),
            ObservedRejection.Replace("128000)", "127000)"),
            ObservedRejection.Replace("131072)", "999999999999999999999)"),
            "Invalid tool schema. " + ObservedRejection
        ];
        foreach (var streaming in new[] { false, true })
        {
            foreach (var message in messages) yield return [streaming, 400, message];
            yield return [streaming, 422, ObservedRejection];
            yield return [streaming, 403, ObservedRejection];
        }
    }

    [Theory]
    [MemberData(nameof(UnrecoverableRejections))]
    public async Task Input_overflow_unrelated_or_ambiguous_errors_are_not_retried(bool streaming, int status, string message)
    {
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            requests++;
            return Task.FromResult(Reject(message, (HttpStatusCode)status));
        }));
        using var client = Create(http);
        await Assert.ThrowsAsync<ClientResultException>(() => Run(client, [new(ChatRole.User, "task")], new() { MaxOutputTokens = 128000 }, streaming));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"error\":\"invalid request\"}")]
    [InlineData("{\"error\":{\"message\":123}}")]
    [InlineData("not JSON")]
    public async Task Unknown_error_envelopes_preserve_the_original_failure(string body)
    {
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }));
        using var client = Create(http);
        await Assert.ThrowsAsync<ClientResultException>(() => Run(client, [new(ChatRole.User, "task")], new() { MaxOutputTokens = 128000 }, false));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adjustment_reauthorizes_before_sending_the_second_request(bool streaming)
    {
        var requests = 0;
        var authorizations = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            requests++;
            return Task.FromResult(Reject(ObservedRejection));
        }));
        using var client = Create(http, authorize: true);
        using var scope = new ProviderDispatchScope(_ => ++authorizations == 1
            ? Task.CompletedTask : throw new ProviderDispatchDeniedException());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => Run(client, [new(ChatRole.User, "task")], new() { MaxOutputTokens = 128000 }, streaming));
        Assert.Equal(2, authorizations);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_rejection_does_not_start_a_retry(bool streaming)
    {
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            requests++;
            cancellation.Cancel();
            return Task.FromResult(Reject(ObservedRejection));
        }));
        using var client = Create(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(client, [new(ChatRole.User, "task")], new() { MaxOutputTokens = 128000 }, streaming, cancellation.Token));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("reasoning")]
    [InlineData("text")]
    [InlineData("tool")]
    [InlineData("usage")]
    public async Task An_error_after_any_stream_update_never_replays_generation(string kind)
    {
        using var http = new HttpClient(new RecordingHandler(_ => Task.FromResult(Reject(ObservedRejection))));
        using var wireClient = Create(http);
        var rejection = await Assert.ThrowsAsync<ClientResultException>(() => wireClient.GetResponseAsync([new(ChatRole.User, "task")]));
        var update = new ChatResponseUpdate(ChatRole.Assistant, "");
        update.Contents = kind switch
        {
            "reasoning" => [new TextReasoningContent("thinking")],
            "text" => [new TextContent("partial")],
            "tool" => [new FunctionCallContent("id", "inspect", new Dictionary<string, object?>())],
            "usage" => [new UsageContent(new() { OutputTokenCount = 1 })],
            _ => []
        };
        var inner = new StartedClient(update, rejection);
        using var client = new ProviderOutputLimitChatClient(inner);
        var seen = new List<ChatResponseUpdate>();
        var error = await Assert.ThrowsAsync<ClientResultException>(async () =>
        {
            await foreach (var item in client.GetStreamingResponseAsync([], new() { MaxOutputTokens = 128000 })) seen.Add(item);
        });
        Assert.Same(rejection, error);
        Assert.Single(seen);
        Assert.Equal(1, inner.Requests);
    }

    private static IChatClient Create(HttpClient http, bool supportsStreaming = true, bool authorize = false)
    {
        var options = new OpenAIClientOptions { Endpoint = new("https://provider.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(http) };
        if (authorize) options.AddPolicy(new ProviderDispatchPolicy(_ => Task.CompletedTask), PipelinePosition.BeforeTransport);
        return OpenAiCompatibleLlmProviderFactory.AdaptChatClient(new OpenAI.Chat.ChatClient("model", new ApiKeyCredential("test"), options), supportsStreaming);
    }

    private static int OutputLimit(JsonElement body) => body.TryGetProperty("max_completion_tokens", out var value)
        ? value.GetInt32() : body.GetProperty("max_tokens").GetInt32();

    private static Task<ChatResponse> Run(IChatClient client, IEnumerable<ChatMessage> messages, ChatOptions options,
        bool streaming, CancellationToken token = default) => streaming
        ? client.GetStreamingResponseAsync(messages, options, token).ToChatResponseAsync(token)
        : client.GetResponseAsync(messages, options, token);

    private static HttpResponseMessage Reject(string message, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { type = "invalid_request_error", message } }), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Reply(bool streaming)
    {
        const string completion = """
            {"id":"done","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"Done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":32000,"completion_tokens":15000,"total_tokens":47000}}
            """;
        const string events = """
            data: {"id":"done","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{"role":"assistant","content":"Done"},"finish_reason":"stop"}]}

            data: {"id":"done","object":"chat.completion.chunk","created":1,"model":"model","choices":[],"usage":{"prompt_tokens":32000,"completion_tokens":15000,"total_tokens":47000}}

            data: [DONE]


            """;
        return new(HttpStatusCode.OK) { Content = new StringContent(streaming ? events : completion,
            Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class StartedClient(ChatResponseUpdate update, Exception error) : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            await Task.Yield();
            yield return update;
            throw error;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
