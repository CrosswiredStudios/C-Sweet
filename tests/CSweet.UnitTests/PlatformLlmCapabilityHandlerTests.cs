using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var chatTurnId = Guid.NewGuid();
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
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = employeeId,
            OrganizationId = organizationId,
            AgentInstallationId = installationId,
            DisplayName = "Test employee",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var handler = new PlatformLlmCapabilityHandler(
            db,
            new StreamingProviderFactory(),
            new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new
            {
                providerProfileId = providerId,
                model = "test-model",
                messages = new[] { new { role = "user", text = "<memory_context>remembered</memory_context>Hello" } },
                instructions = "Be concise.",
                tools = new[]
                {
                    new
                    {
                        name = "lookup",
                        description = "Look up a value.",
                        jsonSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                    }
                },
                telemetry = new
                {
                    conversationId,
                    chatTurnId,
                    invocationKind = "tool-followup",
                    invocationSequence = 2,
                    memoryCharacterCount = 10
                }
            }, JsonOptions))
        };
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"),
            "test-agent",
            installationId.ToString("D"),
            organizationId.ToString("D"),
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

        var runLog = Assert.Single(await db.AgentRunLogs.AsNoTracking().ToListAsync());
        Assert.Equal(organizationId, runLog.OrganizationId);
        Assert.Equal(employeeId, runLog.EmployeeId);
        Assert.Equal(installationId, runLog.AgentInstallationId);
        Assert.Equal("test-agent", runLog.AgentKey);
        Assert.Equal(providerId, runLog.ProviderProfileId);
        Assert.Equal("test-model", runLog.Model);
        Assert.Equal("Completed", runLog.Status);
        Assert.Equal(12, runLog.TokenInputCount);
        Assert.Equal(34, runLog.TokenOutputCount);
        Assert.Equal(conversationId, runLog.ConversationId);
        Assert.Equal(chatTurnId, runLog.ChatTurnId);
        Assert.Equal("tool-followup", runLog.InvocationKind);
        Assert.Equal(2, runLog.InvocationSequence);
        Assert.Equal(10, runLog.PromptMemoryCharacters);
        Assert.True(runLog.PromptMessageCharacters > 0);
        Assert.True(runLog.PromptInstructionCharacters > 0);
        Assert.True(runLog.PromptToolCharacters > 0);
        Assert.Equal(5, runLog.TokenCachedInputCount);

        var globalUsage = await new LlmTokenUsageService(db).GetSummaryAsync();
        var agentUsage = Assert.Single(globalUsage.Agents, x => x.AgentKey == "test-agent");
        Assert.Equal(46, agentUsage.Usage.TotalTokens);
    }

    [Fact]
    public async Task StreamAsync_PersistsPartialUsageWhenProviderStreamFails()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var providerId = await AddProviderAsync(db);
        var handler = new PlatformLlmCapabilityHandler(
            db,
            new StreamingProviderFactory(new ThrowingAfterUsageChatClient()),
            new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            NullLogger<PlatformLlmCapabilityHandler>.Instance);

        var results = await ReadAsync(handler, providerId);

        Assert.False(Assert.Single(results).Succeeded);
        var log = Assert.Single(await db.AgentRunLogs.AsNoTracking().ToListAsync());
        Assert.Equal("Failed", log.Status);
        Assert.Equal(8, log.TokenInputCount);
        Assert.Equal(3, log.TokenOutputCount);
    }

    [Fact]
    public async Task StreamAsync_DoesNotFailInferenceWhenTelemetryPersistenceFails()
    {
        var interceptor = new ThrowingRunLogSaveInterceptor();
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options);
        var providerId = await AddProviderAsync(db);
        interceptor.Enabled = true;
        var handler = new PlatformLlmCapabilityHandler(
            db,
            new StreamingProviderFactory(),
            new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            NullLogger<PlatformLlmCapabilityHandler>.Instance);

        var results = await ReadAsync(handler, providerId);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Empty(await db.AgentRunLogs.AsNoTracking().ToListAsync());
    }

    private static async Task<Guid> AddProviderAsync(CSweetDbContext db)
    {
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
        return providerId;
    }

    private static async Task<IReadOnlyList<CapabilityResult>> ReadAsync(
        PlatformLlmCapabilityHandler handler,
        Guid providerId)
    {
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
        await foreach (var result in handler.StreamAsync(session, request, CancellationToken.None))
            results.Add(result);
        return results;
    }

    private sealed class StreamingProviderFactory(IChatClient? client = null) : ILlmProviderFactory
    {
        private readonly IChatClient _client = client ?? new StreamingChatClient();

        public Task<IChatClient> CreateChatClientAsync(
            Guid providerProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_client);

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
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 12,
                    OutputTokenCount = 34,
                    AdditionalCounts = new AdditionalPropertiesDictionary<long>
                    {
                        ["input_cached_tokens"] = 5
                    }
                })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingAfterUsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 8, OutputTokenCount = 3 })]);
            throw new InvalidOperationException("Provider stream failed.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingRunLogSaveInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.ChangeTracker.Entries<AgentRunLog>()
                    .Any(x => x.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException("Telemetry store unavailable.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
