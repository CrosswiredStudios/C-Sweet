using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class OpenAiCompatibleLlmProviderFactoryTests
{
    [Theory]
    [InlineData(false, null, 128000)]
    [InlineData(true, null, 128000)]
    [InlineData(false, 4096, 4096)]
    [InlineData(true, 4096, 4096)]
    public async Task Provider_output_default_reaches_both_chat_paths_without_mutating_caller_options(
        bool streaming, int? requested, int expected)
    {
        var inner = new CapturingChatClient();
        using var client = OpenAiCompatibleLlmProviderFactory.ApplyProviderDefaults(inner, 128000);
        var options = new ChatOptions { MaxOutputTokens = requested };
        ChatMessage[] messages = [new(ChatRole.User, "hello")];

        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(messages, options)) { }
        }
        else
        {
            await client.GetResponseAsync(messages, options);
        }

        Assert.Equal(expected, inner.Options?.MaxOutputTokens);
        Assert.Equal(requested, options.MaxOutputTokens);
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(LlmProviderType.Custom)]
    [InlineData(LlmProviderType.OpenAiCompatible)]
    public async Task Keyless_compatible_provider_can_create_the_agent_chat_client(LlmProviderType providerType)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var profile = new LlmProviderProfile
        {
            Id = Guid.NewGuid(), Name = "Ninfer", ProviderType = providerType,
            BaseUrl = "http://localhost:8001/v1", DefaultChatModel = "qwen3.8-27b-nvfp4", IsEnabled = true
        };
        db.LlmProviderProfiles.Add(profile);
        await db.SaveChangesAsync();
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(),
            NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance);

        using var client = await factory.CreateChatClientAsync(profile.Id);
        Assert.NotNull(client);
    }
}
