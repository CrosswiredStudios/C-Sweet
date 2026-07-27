using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class PlatformLlmCapabilityHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StreamAsync_AggregatesProviderUpdatesIntoSingleMcpToolResult()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var providerId = Guid.NewGuid();
        db.LlmProviderProfiles.Add(new LlmProviderProfile
        {
            Id = providerId,
            Name = "Test provider",
            ProviderType = LlmProviderType.LmStudio,
            BaseUrl = "http://localhost:1234/v1",
            DefaultChatModel = "test-model",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var handler = new PlatformLlmCapabilityHandler(
            db,
            new StreamingProviderFactory(),
            new AgentEmployeeIdentityResolver(db),
            NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new
            {
                providerProfileId = providerId,
                model = "test-model",
                messages = new[] { new { role = "user", text = "Hello" } }
            }, JsonOptions))
        };
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"),
            "test-agent",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(
                new HashSet<string>(),
                new HashSet<string>(),
                new HashSet<string>([PlatformCapabilities.LlmChatStream], StringComparer.Ordinal),
                Revision: 1));

        var results = new List<CapabilityResult>();
        await foreach (var streamedResult in handler.StreamAsync(session, request, CancellationToken.None))
            results.Add(streamedResult);

        var result = Assert.Single(results);
        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.HasMore);
        using var payload = JsonDocument.Parse(result.Payload.ToByteArray());
        Assert.Equal("Hello world", payload.RootElement.GetProperty("text").GetString());
        Assert.Equal(
            ["Hello ", "world"],
            payload.RootElement.GetProperty("contents")
                .EnumerateArray()
                .Select(x => x.GetProperty("text").GetString()!)
                .ToArray());
    }

    private sealed class StreamingProviderFactory : ILlmProviderFactory
    {
        public Task<IChatClient> CreateChatClientAsync(
            Guid providerProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(new StreamingChatClient());

        public Task<IChatClient> CreateChatClientAsync(
            Guid providerProfileId,
            string? model,
            CancellationToken cancellationToken = default) =>
            CreateChatClientAsync(providerProfileId, cancellationToken);
    }

    private sealed class StreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello world")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Hello ")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("world")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
