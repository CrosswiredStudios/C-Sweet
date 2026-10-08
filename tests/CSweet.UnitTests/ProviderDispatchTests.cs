using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace CSweet.UnitTests;

public sealed class ProviderDispatchTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProviderChangeAfterClientConstructionDeniesBeforeTransport(bool streaming, bool fallback)
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var profile = new LlmProviderProfile { Id = Guid.NewGuid(), Name = "Dispatch", ProviderType = LlmProviderType.Custom,
            BaseUrl = "https://provider.invalid/v1/", DefaultChatModel = "test", SupportsStreaming = !fallback, IsEnabled = true };
        db.LlmProviderProfiles.Add(profile); await db.SaveChangesAsync();
        using var transport = new WireHandler(); using var http = new HttpClient(transport);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(profile.Id);
        Assert.NotNull(client.GetService<IProviderDispatchTransport>());
        await using (var revoked = new CSweetDbContext(options))
        {
            var current = await revoked.LlmProviderProfiles.SingleAsync(); current.IsEnabled = false; await revoked.SaveChangesAsync();
        }
        var usage = new UsageCapturingChatClient(client);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => Call(usage, streaming));
        Assert.Equal(0, transport.Calls); Assert.Null(Assert.Single(usage.Calls).ProviderStartedAt);
        Assert.Equal("Denied", usage.Calls[0].Status);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("output")]
    [InlineData("revision")]
    [InlineData("secret")]
    public async Task FactoryChecksCurrentDispatchConfigurationAndCredential(string change)
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var secrets = new InMemoryLlmProviderSecretStore(); await secrets.StoreAsync("key", "old-test-key");
        var profile = new LlmProviderProfile { Id = Guid.NewGuid(), Name = "Dispatch", ProviderType = LlmProviderType.Custom,
            BaseUrl = "https://provider.invalid/v1/", DefaultChatModel = "test", IsEnabled = true, ApiKeySecretName = "key" };
        db.Add(profile); await db.SaveChangesAsync();
        using var transport = new WireHandler(); using var http = new HttpClient(transport);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, secrets, NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(profile.Id);
        await using (var edited = new CSweetDbContext(options))
        {
            var current = await edited.LlmProviderProfiles.SingleAsync();
            if (change == "endpoint") current.BaseUrl = "https://other.invalid/v1/";
            if (change == "model") current.DefaultChatModel = "different";
            if (change == "output") current.MaxOutputTokens = 10;
            if (change == "revision") current.UpdatedAt = DateTimeOffset.UtcNow;
            if (change == "secret") await secrets.StoreAsync("key", "rotated-test-key");
            await edited.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => Call(client, false));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EverySdkRetryReauthorizesSourcesAndPreservesEvidenceOfFirstDispatch(bool streaming, bool fallback)
    {
        var allowed = true;
        using var transport = new WireHandler { FirstResponse = HttpStatusCode.ServiceUnavailable, AfterSend = () => allowed = false };
        using var http = new HttpClient(transport);
        var options = new OpenAIClientOptions { Endpoint = new("https://provider.invalid/v1/"), Transport = new HttpClientPipelineTransport(http) };
        options.AddPolicy(new ProviderDispatchPolicy(_ => Task.CompletedTask), PipelinePosition.BeforeTransport);
        using var client = new DispatchManagedChatClient(OpenAiCompatibleLlmProviderFactory.AdaptChatClient(
            new OpenAI.Chat.ChatClient("test", new ApiKeyCredential("test"), options), !fallback));
        var checks = 0;
        var usage = new UsageCapturingChatClient(client, authorizeDispatch: _ =>
        {
            checks++; if (!allowed) throw new ProviderDispatchDeniedException(); return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => Call(usage, streaming));
        Assert.Equal(1, transport.Calls); Assert.True(checks >= 3);
        Assert.NotNull(Assert.Single(usage.Calls).ProviderStartedAt); Assert.Equal("Denied", usage.Calls[0].Status);
    }

    [Fact]
    public async Task FactoryRechecksProviderOnSdkRetry()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var profile = new LlmProviderProfile { Id = Guid.NewGuid(), Name = "Dispatch", ProviderType = LlmProviderType.Custom,
            BaseUrl = "https://provider.invalid/v1/", DefaultChatModel = "test", IsEnabled = true };
        db.Add(profile); await db.SaveChangesAsync();
        using var transport = new WireHandler { FirstResponse = HttpStatusCode.ServiceUnavailable, AfterSend = () =>
        {
            using var revoked = new CSweetDbContext(options);
            revoked.LlmProviderProfiles.Single().IsEnabled = false; revoked.SaveChanges();
        } };
        using var http = new HttpClient(transport);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(), NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance)
            { TransportOverride = new HttpClientPipelineTransport(http) };
        using var client = await factory.CreateChatClientAsync(profile.Id);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => Call(client, false));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task ResponseCorrectionAlsoReauthorizesAtTransport()
    {
        var allowed = true;
        using var transport = new WireHandler { EmptyFirstResponse = true, AfterSend = () => allowed = false };
        using var http = new HttpClient(transport);
        var options = new OpenAIClientOptions { Endpoint = new("https://provider.invalid/v1/"), Transport = new HttpClientPipelineTransport(http) };
        options.AddPolicy(new ProviderDispatchPolicy(_ => Task.CompletedTask), PipelinePosition.BeforeTransport);
        using var client = OpenAiCompatibleLlmProviderFactory.AdaptChatClient(new OpenAI.Chat.ChatClient("test", new ApiKeyCredential("test"), options));
        using var scope = new ProviderDispatchScope(_ => allowed ? Task.CompletedTask : throw new ProviderDispatchDeniedException());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => Call(client, false));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task GuardsAreScopedNestedAndCannotBeReusedAfterDisposal()
    {
        var calls = new List<string>();
        using (var outer = new ProviderDispatchScope(_ => { calls.Add("outer"); return Task.CompletedTask; }))
        {
            using (var inner = new ProviderDispatchScope(_ => { calls.Add("inner"); return Task.CompletedTask; }))
                await ProviderDispatchScope.AuthorizeCurrentAsync(default);
            await ProviderDispatchScope.AuthorizeCurrentAsync(default);
        }
        await ProviderDispatchScope.AuthorizeCurrentAsync(default);
        Assert.Equal(new[] { "inner", "outer", "outer" }, calls);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task orphan;
        using (var scope = new ProviderDispatchScope(_ => Task.CompletedTask))
            orphan = Task.Run(async () => { await gate.Task; await ProviderDispatchScope.AuthorizeCurrentAsync(default); });
        gate.SetResult(); await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => orphan);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var denied = Task.Run(async () =>
        {
            using var scope = new ProviderDispatchScope(_ => throw new ProviderDispatchDeniedException());
            started.SetResult(); await release.Task;
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => ProviderDispatchScope.AuthorizeCurrentAsync(default));
        });
        await started.Task; await ProviderDispatchScope.AuthorizeCurrentAsync(default); release.SetResult(); await denied;
    }

    private static async Task Call(IChatClient client, bool streaming)
    {
        ChatMessage[] messages = [new(ChatRole.User, "Private memory fixture")];
        if (streaming) await client.GetStreamingResponseAsync(messages).ToChatResponseAsync();
        else await client.GetResponseAsync(messages);
    }

    private sealed class WireHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode? FirstResponse { get; init; }
        public bool EmptyFirstResponse { get; init; }
        public Action? AfterSend { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; AfterSend?.Invoke();
            var status = Calls == 1 ? FirstResponse ?? HttpStatusCode.OK : HttpStatusCode.OK;
            var content = EmptyFirstResponse && Calls == 1 ? "" : "Ready";
            var response = new HttpResponseMessage(status) { Content = new StringContent(
                $$"""{"id":"test","object":"chat.completion","created":1,"model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"{{content}}"},"finish_reason":"stop"}]}""",
                Encoding.UTF8, "application/json") };
            if (status != HttpStatusCode.OK) response.Headers.TryAddWithoutValidation("Retry-After", "0");
            return Task.FromResult(response);
        }
    }
}
