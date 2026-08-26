using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.AI.Providers;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
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
    public async Task StreamAsync_ResolvesVerifiedOpaqueAttachmentAndDeniesForgedDigest()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var providerId = await AddProviderAsync(db);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("# Reference\n\nA clockwork ocean world.");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = employeeId, OrganizationId = organizationId, AgentInstallationId = installationId,
            DisplayName = "Creative Director", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.CoreConversations.Add(new Conversation
        {
            Id = conversationId, OrganizationId = organizationId,
            InitiatedByOrganizationUserId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ConversationParticipants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, OrganizationUserId = employeeId,
            Role = ConversationParticipantRole.Member, JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var resolver = new TestAttachmentResolver(
            attachmentId,
            new CommunicationMessageAttachmentResponse(
                attachmentId, messageId, "world.md", "text/markdown", bytes.Length, digest),
            conversationId,
            bytes);
        var handler = new PlatformLlmCapabilityHandler(
            db, new StreamingProviderFactory(), new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [resolver],
            NullLogger<PlatformLlmCapabilityHandler>.Instance);
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"), "creative-director", installationId.ToString("D"),
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(
                new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([PlatformCapabilities.LlmChatStream], StringComparer.Ordinal), 1));

        RequestCapability CreateRequest(string suppliedDigest) => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new
            {
                providerProfileId = providerId,
                model = "test-model",
                messages = new[] { new { role = "user", contents = new[] { new
                {
                    kind = "media_reference", attachmentId, messageId, conversationId,
                    fileName = "world.md", contentType = "text/markdown",
                    sizeBytes = bytes.Length, sha256 = suppliedDigest
                } } } },
                telemetry = new { conversationId, chatTurnId = Guid.NewGuid(), invocationKind = "creative-pitch" }
            }, JsonOptions))
        };

        var accepted = new List<CapabilityResult>();
        await foreach (var result in handler.StreamAsync(session, CreateRequest(digest), CancellationToken.None))
            accepted.Add(result);
        Assert.True(resolver.ResolutionCount > 0);
        Assert.All(accepted, result => Assert.True(result.Succeeded, result.Error));

        var denied = new List<CapabilityResult>();
        await foreach (var result in handler.StreamAsync(session, CreateRequest(new string('0', 64)), CancellationToken.None))
            denied.Add(result);
        var failure = Assert.Single(denied);
        Assert.False(failure.Succeeded);
        Assert.Contains("metadata verification", failure.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamAsync_ForwardsInterleavedProviderUpdatesInExactOrder()
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
            [],
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

        Assert.Equal(7, results.Count);
        Assert.Equal(Enumerable.Range(0, results.Count), results.Select(result => result.Sequence));
        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        Assert.All(results.Take(results.Count - 1), result => Assert.True(result.HasMore));
        Assert.False(results[^1].HasMore);
        var chunks = results.Select(result =>
        {
            using var document = JsonDocument.Parse(result.Payload.ToByteArray());
            return document.RootElement.Clone();
        }).ToList();
        Assert.Equal(
            ["reasoning", "text", "function_call", "function_result", "text"],
            chunks.SelectMany(chunk => chunk.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array
                    ? contents.EnumerateArray().ToArray()
                    : [])
                .Select(content => content.GetProperty("kind").GetString()));
        Assert.Equal("Considering the lookup. ", chunks[0].GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.Equal("encrypted-roundtrip-only", chunks[0].GetProperty("contents")[0].GetProperty("protectedData").GetString());
        Assert.Equal("Hello world", string.Concat(chunks.Select(chunk =>
            chunk.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null)));
        Assert.Equal(12, chunks[^2].GetProperty("inputTokenCount").GetInt64());
        Assert.Equal(34, chunks[^2].GetProperty("outputTokenCount").GetInt64());

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
            [],
            NullLogger<PlatformLlmCapabilityHandler>.Instance);

        var results = await ReadAsync(handler, providerId);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Succeeded);
        Assert.True(results[0].HasMore);
        Assert.False(results[1].Succeeded);
        Assert.False(results[1].HasMore);
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
            [],
            NullLogger<PlatformLlmCapabilityHandler>.Instance);

        var results = await ReadAsync(handler, providerId);

        Assert.Equal(7, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.False(results[^1].HasMore);
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

    private sealed class TestAttachmentResolver(
        Guid attachmentId,
        CommunicationMessageAttachmentResponse descriptor,
        Guid conversationId,
        byte[] bytes) : IConversationAttachmentSourceResolver
    {
        public string Source => "test";
        public int ResolutionCount { get; private set; }

        public Task<ResolvedConversationAttachment?> ResolveAsync(
            Guid organizationId,
            Guid requestedAttachmentId,
            CancellationToken cancellationToken = default)
        {
            if (requestedAttachmentId != attachmentId)
                return Task.FromResult<ResolvedConversationAttachment?>(null);
            ResolutionCount++;
            return Task.FromResult<ResolvedConversationAttachment?>(new(
                descriptor, conversationId, Guid.NewGuid(), new MemoryStream(bytes, writable: false)));
        }
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
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new TextReasoningContent("Considering the lookup. ") { ProtectedData = "encrypted-roundtrip-only" }]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Hello ")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["key"] = "value" })]);
            yield return new ChatResponseUpdate(ChatRole.Tool,
                [new FunctionResultContent("call-1", JsonSerializer.SerializeToElement(new { value = 42 }))]);
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
