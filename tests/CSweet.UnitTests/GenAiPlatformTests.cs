using CSweet.Application.GenAi;
using CSweet.Agent.Contracts.Grpc;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.GenAi;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Google.Protobuf;

namespace CSweet.UnitTests;

public sealed class GenAiPlatformTests
{
    [Fact]
    public void Capabilities_AreLeastPrivilegeAndMapIndependently()
    {
        Assert.Equal(4, GenAiCapabilities.Operations.Count);
        Assert.Equal(GenAiOperationType.ImageGeneration, GenAiCapabilities.ToOperation(GenAiCapabilities.ImageGenerate));
        Assert.Equal(GenAiOperationType.ImageEditing, GenAiCapabilities.ToOperation(GenAiCapabilities.ImageEdit));
        Assert.Equal(GenAiOperationType.VideoGeneration, GenAiCapabilities.ToOperation(GenAiCapabilities.VideoGenerate));
        Assert.Equal(GenAiOperationType.VideoEditing, GenAiCapabilities.ToOperation(GenAiCapabilities.VideoEdit));
        Assert.DoesNotContain(GenAiCapabilities.JobRead, GenAiCapabilities.Operations);
        Assert.DoesNotContain(GenAiCapabilities.JobCancel, GenAiCapabilities.Operations);
    }

    [Fact]
    public async Task Dispatcher_DoesNotTreatOneMediaGrantAsAnUmbrellaGrant()
    {
        var handler = new CountingHandler();
        var dispatcher = new PlatformCapabilityDispatcher([handler]);
        var session = new AgentSession(
            "session", "agent", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>(), new HashSet<string>([GenAiCapabilities.ImageGenerate], StringComparer.Ordinal)));

        var allowed = await InvokeAsync(dispatcher, session, GenAiCapabilities.ImageGenerate);
        var denied = await InvokeAsync(dispatcher, session, GenAiCapabilities.VideoGenerate);
        var ownJobRead = await InvokeAsync(dispatcher, session, GenAiCapabilities.JobRead);

        Assert.True(allowed.Succeeded);
        Assert.False(denied.Succeeded);
        Assert.Contains("may not request", denied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(ownJobRead.Succeeded);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ProviderAndOperation_CanBeSavedWithoutExposingSecret_AndFirstOperationBecomesDefault()
    {
        await using var db = CreateDb();
        var secrets = new InMemoryLlmProviderSecretStore();
        var service = new GenAiProviderProfileService(db, secrets, [new StubAdapter(GenAiProviderType.OpenAi)]);
        var created = await service.CreateAsync(new("OpenAI Images", GenAiProviderType.OpenAi, "https://api.openai.com", "secret-value"));

        Assert.True(created.Succeeded);
        Assert.True(created.Profile?.HasApiKey);
        Assert.DoesNotContain("secret-value", System.Text.Json.JsonSerializer.Serialize(created));

        var operation = await service.SaveOperationAsync(created.Profile!.Id, null,
            new(GenAiOperationType.ImageGeneration, "Production image", "gpt-image-2", null, null, null, true));

        Assert.True(operation.Succeeded);
        Assert.True(operation.Operation?.IsDefault);
        Assert.Equal(operation.Operation!.Id,
            await db.GenAiOperationDefaults.Select(x => x.OperationConfigurationId).SingleAsync());
    }

    [Fact]
    public async Task JobStart_IsIdempotent_AndLookupIsOrganizationAndInstallationScoped()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var provider = new GenAiProviderProfile
        {
            Id = Guid.NewGuid(), Name = "Provider", ProviderType = GenAiProviderType.OpenAi,
            BaseUrl = "https://api.openai.com", IsEnabled = true, CreatedAt = now, UpdatedAt = now
        };
        var operation = new GenAiOperationConfiguration
        {
            Id = Guid.NewGuid(), ProviderProfileId = provider.Id, ProviderProfile = provider,
            OperationType = GenAiOperationType.ImageGeneration, Name = "Default", ModelId = "gpt-image-2",
            IsEnabled = true, CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(provider, operation, new GenAiOperationDefault
        {
            Id = Guid.NewGuid(), OperationType = GenAiOperationType.ImageGeneration,
            OperationConfigurationId = operation.Id, OperationConfiguration = operation, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var service = new GenAiJobService(db);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var request = new GenAiMediaRequest("Create a product image", IdempotencyKey: "same-request");

        var first = await service.StartAsync(organizationId, installationId, GenAiOperationType.ImageGeneration, request);
        var second = await service.StartAsync(organizationId, installationId, GenAiOperationType.ImageGeneration, request);

        Assert.Equal(first.Id, second.Id);
        Assert.NotNull(await service.GetAsync(first.Id, organizationId, installationId));
        Assert.Null(await service.GetAsync(first.Id, Guid.NewGuid(), installationId));
        Assert.Null(await service.GetAsync(first.Id, organizationId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Editing_RejectsMissingSourceAssets()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var provider = new GenAiProviderProfile
        {
            Id = Guid.NewGuid(), Name = "Provider", ProviderType = GenAiProviderType.GoogleGemini,
            BaseUrl = "https://generativelanguage.googleapis.com", IsEnabled = true, CreatedAt = now, UpdatedAt = now
        };
        var operation = new GenAiOperationConfiguration
        {
            Id = Guid.NewGuid(), ProviderProfileId = provider.Id, ProviderProfile = provider,
            OperationType = GenAiOperationType.ImageEditing, Name = "Edit", ModelId = "image-model",
            IsEnabled = true, CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(provider, operation, new GenAiOperationDefault
        {
            Id = Guid.NewGuid(), OperationType = operation.OperationType,
            OperationConfigurationId = operation.Id, OperationConfiguration = operation, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GenAiJobService(db).StartAsync(Guid.NewGuid(), Guid.NewGuid(), operation.OperationType, new("Edit it")));

        Assert.Contains("source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<CapabilityResult> InvokeAsync(IPlatformCapabilityDispatcher dispatcher, AgentSession session, string capability)
    {
        await foreach (var result in dispatcher.InvokeAsync(session, new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"), Capability = capability, ContentType = "application/json",
            Payload = ByteString.CopyFromUtf8("{}")
        }, CancellationToken.None))
            return result;
        throw new InvalidOperationException("Dispatcher returned no result.");
    }

    private sealed class CountingHandler : IPlatformCapabilityHandler
    {
        public int Count { get; private set; }
        public bool CanHandle(string capability) => GenAiCapabilities.Operations.Contains(capability) ||
            capability is GenAiCapabilities.JobRead or GenAiCapabilities.JobCancel;
        public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Count++;
            yield return new CapabilityResult
            {
                RequestId = request.RequestId, Succeeded = true, ContentType = "application/json",
                Payload = ByteString.CopyFromUtf8("{}")
            };
            await Task.CompletedTask;
        }
    }

    private sealed class StubAdapter(GenAiProviderType type) : IGenAiProviderAdapter
    {
        public GenAiProviderType ProviderType => type;
        public Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new GenAiConnectionTestResponse(true, null, "Connected.", DateTimeOffset.UtcNow));
        public Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, GenAiMediaRequest request,
            IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, string providerJobId, string? apiKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
