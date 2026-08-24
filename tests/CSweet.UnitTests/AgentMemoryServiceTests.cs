using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentMemoryServiceTests
{
    [Fact]
    public async Task RelationshipMemory_IsRecalledAcrossConversationsAndBrowsable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-agent-memory-{Guid.NewGuid():N}.db");
        await using var store = new SqliteMemoryStore(path);
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        try
        {
            var organizationId = Guid.NewGuid();
            var humanId = Guid.NewGuid();
            var otherHumanId = Guid.NewGuid();
            var applicationUserId = Guid.NewGuid();
            var employeeId = Guid.NewGuid();
            var installationId = Guid.NewGuid();
            var packageId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var providerId = Guid.NewGuid();
            db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.CoreOrganizationUsers.Add(new OrganizationUser { Id = humanId, OrganizationId = organizationId, ApplicationUserId = applicationUserId, DisplayName = "Owner", EmployeeType = EmployeeType.Human, CreatedAt = DateTimeOffset.UtcNow });
            db.CoreOrganizationUsers.Add(new OrganizationUser { Id = otherHumanId, OrganizationId = organizationId, DisplayName = "Other", EmployeeType = EmployeeType.Human, CreatedAt = DateTimeOffset.UtcNow });
            var package = new AgentPackageVersion { Id = packageId, AgentId = "com.example.assistant", AgentName = "Assistant", Version = "1.0.0" };
            var installation = new AgentInstallation { Id = installationId, PackageVersionId = packageId, PackageVersion = package, BusinessId = organizationId.ToString(), IsEnabled = true };
            var employee = new OrganizationUser { Id = employeeId, OrganizationId = organizationId, DisplayName = "Assistant", EmployeeType = EmployeeType.Agent, AgentInstallationId = installationId, AgentInstallation = installation, CreatedAt = DateTimeOffset.UtcNow };
            db.AgentPackageVersions.Add(package);
            db.AgentInstallations.Add(installation);
            db.CoreOrganizationUsers.Add(employee);
            var first = new Conversation { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentOrganizationUserId = employeeId, InitiatedByOrganizationUserId = humanId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var second = new Conversation { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentOrganizationUserId = employeeId, InitiatedByOrganizationUserId = humanId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var otherRelationship = new Conversation { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentOrganizationUserId = employeeId, InitiatedByOrganizationUserId = otherHumanId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = first.Id, Conversation = first, ChatTurnId = turnId, Role = ConversationRole.User, Content = "My name is Alice.", CreatedAt = DateTimeOffset.UtcNow };
            db.CoreConversations.AddRange(first, second, otherRelationship);
            db.CoreConversationMessages.Add(message);
            db.LlmProviderProfiles.Add(new LlmProviderProfile
            {
                Id = providerId,
                Name = "Test provider",
                ProviderType = LlmProviderType.LmStudio,
                BaseUrl = "http://test-provider/v1",
                DefaultChatModel = string.Empty,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem { Id = Guid.NewGuid(), ConversationMessageId = message.Id, Status = MemoryCaptureStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var providerFactory = new UsageProviderFactory();
            var configurations = new StaticInstallationConfigurationService(installationId, providerId, "test-model");
            var service = new AgentMemoryService(db, store, providerFactory, configurations,
                NullLogger<AgentMemoryService>.Instance);
            await service.CaptureMessageAsync(message.Id);

            var recalled = await service.RecallForConversationAsync(second.Id, "What is my name?");
            var isolatedRecall = await service.RecallForConversationAsync(otherRelationship.Id, "What is my name?");
            var summary = await service.GetSummaryAsync(organizationId, employeeId);
            var page = await service.BrowseAsync(organizationId, employeeId, new(Limit: 20));
            var detail = await service.GetItemAsync(organizationId, employeeId, message.Id);

            Assert.Contains("Alice", recalled);
            Assert.Null(isolatedRecall);
            Assert.True(await service.CanExploreAsync(organizationId, applicationUserId));
            Assert.False(await service.CanExploreAsync(organizationId, Guid.NewGuid()));
            Assert.Equal(1, summary!.EpisodeCount);
            Assert.Contains(page!.Items, item => item.Id == message.Id && item.ConversationId == first.Id);
            Assert.Contains(detail!.RecallUses!, use => use.ConversationId == second.Id && use.UserId == humanId);
            var registered = Assert.Single(await db.AgentMemoryNamespaces.ToListAsync());
            Assert.Equal(employeeId, registered.EmployeeId);
            Assert.Equal(humanId, registered.UserId);

            Assert.Equal(1, await service.ProcessPendingAsync());
            var enrichmentRun = Assert.Single(await db.AgentRunLogs.ToListAsync());
            Assert.Equal("memory-enrichment", enrichmentRun.InvocationKind);
            Assert.Equal(first.Id, enrichmentRun.ConversationId);
            Assert.Equal(turnId, enrichmentRun.ChatTurnId);
            Assert.Equal(providerId, enrichmentRun.ProviderProfileId);
            Assert.Equal(23, enrichmentRun.TokenInputCount);
            Assert.Equal(7, enrichmentRun.TokenOutputCount);
            Assert.True(enrichmentRun.PromptMessageCharacters > message.Content.Length);
            Assert.Equal(providerId, providerFactory.SelectedProviderId);
            Assert.Equal("test-model", providerFactory.SelectedModel);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private sealed class UsageProviderFactory : ILlmProviderFactory
    {
        public Guid? SelectedProviderId { get; private set; }
        public string? SelectedModel { get; private set; }

        public Task<IChatClient> CreateChatClientAsync(
            Guid providerProfileId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Memory enrichment must select the provider's configured chat model.");

        public Task<IChatClient> CreateChatClientAsync(
            Guid providerProfileId,
            string? model,
            CancellationToken cancellationToken = default)
        {
            SelectedProviderId = providerProfileId;
            SelectedModel = model;
            return Task.FromResult<IChatClient>(new UsageChatClient());
        }
    }

    private sealed class StaticInstallationConfigurationService(
        Guid installationId,
        Guid providerId,
        string model) : IAgentInstallationConfigurationService
    {
        private readonly AgentInstallationConfigurationSnapshot _configuration = new(
            installationId,
            "1",
            new Dictionary<string, JsonElement>
            {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId.ToString("D")),
                ["llmModel"] = JsonSerializer.SerializeToElement(model)
            },
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        public Task<AgentInstallationConfigurationSnapshot?> GetAsync(
            Guid requestedInstallationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentInstallationConfigurationSnapshot?>(
                requestedInstallationId == installationId ? _configuration : null);

        public Task<AgentInstallationConfigurationSnapshot> SaveAsync(
            Guid requestedInstallationId,
            string schemaVersion,
            IReadOnlyDictionary<string, JsonElement> settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"entities\":[],\"claims\":[],\"edges\":[],\"procedures\":[]}"))
            {
                Usage = new UsageDetails { InputTokenCount = 23, OutputTokenCount = 7 }
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
