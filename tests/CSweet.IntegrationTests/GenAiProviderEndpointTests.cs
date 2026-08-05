using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using CSweet.AI.Providers;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public sealed class GenAiProviderEndpointTests
{
    [Fact]
    public async Task ProviderAndApprovedOperation_CanBeCreatedListedAndMadeDefault()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/genai-provider-profiles",
            new CreateGenAiProviderProfileRequest("OpenAI Images", GenAiProviderType.OpenAi, "https://api.openai.com", "secret-key"));
        var createResult = await createResponse.Content.ReadFromJsonAsync<GenAiActionResponse>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(createResult?.Profile);
        Assert.True(createResult.Profile.HasApiKey);
        Assert.DoesNotContain("secret-key", JsonSerializer.Serialize(createResult), StringComparison.Ordinal);

        var operationResponse = await client.PostAsJsonAsync(
            $"/api/genai-provider-profiles/{createResult.Profile.Id}/operations",
            new SaveGenAiOperationConfigurationRequest(GenAiOperationType.ImageGeneration, "Approved image", "gpt-image-2", null, null, null, true));
        var operationResult = await operationResponse.Content.ReadFromJsonAsync<GenAiActionResponse>();
        var listed = await client.GetFromJsonAsync<IReadOnlyList<GenAiProviderProfileResponse>>("/api/genai-provider-profiles");

        Assert.True(operationResponse.IsSuccessStatusCode, await operationResponse.Content.ReadAsStringAsync());
        Assert.True(operationResult?.Operation?.IsDefault);
        Assert.Single(listed!);
        Assert.Single(listed![0].Operations);
    }

    [Fact]
    public async Task ProviderConnectionTest_RecordsSuccessWithoutReturningSecrets()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/genai-provider-profiles",
            new CreateGenAiProviderProfileRequest("OpenAI Images", GenAiProviderType.OpenAi, "https://api.openai.com", "secret-key"));
        var profile = (await created.Content.ReadFromJsonAsync<GenAiActionResponse>())!.Profile!;

        var response = await client.PostAsync($"/api/genai-provider-profiles/{profile.Id}/test", null);
        var result = await response.Content.ReadFromJsonAsync<GenAiConnectionTestResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result?.Succeeded, await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<CSweetDbContext>()
            .GenAiProviderProfiles.SingleAsync()).LastSuccessfulConnectionAt);
    }

    [Fact]
    public async Task DraftConnectionTest_DoesNotPersistProviderOrReturnSecret()
    {
        var behavior = new AdapterBehavior((_, profile, _, _) => Task.FromResult(
            profile.BaseUrl.Contains("reachable", StringComparison.Ordinal)
                ? Success()
                : Failure()));
        await using var factory = CreateFactory(behavior);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/genai-provider-profiles/test", new TestGenAiProviderConnectionRequest(
            null, GenAiProviderType.OpenAi, "https://reachable.example", "draft-secret"));
        var result = await response.Content.ReadFromJsonAsync<GenAiConnectionTestResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result?.Succeeded);
        Assert.DoesNotContain("draft-secret", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<CSweetDbContext>().GenAiProviderProfiles.ToListAsync());
    }

    [Fact]
    public async Task InvalidCreate_IsRejectedWithoutPersistingProviderOrSecret()
    {
        var behavior = new AdapterBehavior((_, _, _, _) => Task.FromResult(Failure()));
        await using var factory = CreateFactory(behavior);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/genai-provider-profiles",
            new CreateGenAiProviderProfileRequest("Unavailable", GenAiProviderType.OpenAi, "https://unavailable.example", "rejected-secret"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("rejected-secret", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<CSweetDbContext>().GenAiProviderProfiles.ToListAsync());
    }

    [Fact]
    public async Task FailedConnectionUpdate_PreservesExistingProfileSecretAndVerification()
    {
        var allowConnections = true;
        var behavior = new AdapterBehavior((_, _, _, _) => Task.FromResult(allowConnections ? Success() : Failure()));
        await using var factory = CreateFactory(behavior);
        var client = factory.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/api/genai-provider-profiles",
            new CreateGenAiProviderProfileRequest("Original", GenAiProviderType.OpenAi, "https://original.example", "original-secret"));
        var created = (await createdResponse.Content.ReadFromJsonAsync<GenAiActionResponse>())!.Profile!;
        allowConnections = false;

        var updateResponse = await client.PutAsJsonAsync($"/api/genai-provider-profiles/{created.Id}",
            new UpdateGenAiProviderProfileRequest("Changed", GenAiProviderType.OpenAi, "https://changed.example",
                "replacement-secret", ReplaceApiKey: true, IsEnabled: true));

        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var persisted = await db.GenAiProviderProfiles.SingleAsync();
        Assert.Equal("Original", persisted.Name);
        Assert.Equal("https://original.example", persisted.BaseUrl);
        Assert.Equal(created.LastSuccessfulConnectionAt, persisted.LastSuccessfulConnectionAt);
        var secretStore = scope.ServiceProvider.GetRequiredService<ILlmProviderSecretStore>();
        Assert.Equal("original-secret", await secretStore.GetAsync(persisted.ApiKeySecretName!));
    }

    [Fact]
    public async Task NameOnlyUpdate_PreservesVerificationWithoutAnotherConnectionTest()
    {
        var behavior = new AdapterBehavior();
        await using var factory = CreateFactory(behavior);
        var client = factory.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/api/genai-provider-profiles",
            new CreateGenAiProviderProfileRequest("Original", GenAiProviderType.OpenAi, "https://provider.example", "secret"));
        var created = (await createdResponse.Content.ReadFromJsonAsync<GenAiActionResponse>())!.Profile!;
        var testsBeforeUpdate = behavior.Requests.Count;

        var updateResponse = await client.PutAsJsonAsync($"/api/genai-provider-profiles/{created.Id}",
            new UpdateGenAiProviderProfileRequest("Renamed", GenAiProviderType.OpenAi, created.BaseUrl,
                null, ReplaceApiKey: false, IsEnabled: true));
        var updated = (await updateResponse.Content.ReadFromJsonAsync<GenAiActionResponse>())!.Profile!;

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(testsBeforeUpdate, behavior.Requests.Count);
        Assert.Equal(created.LastSuccessfulConnectionAt, updated.LastSuccessfulConnectionAt);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("host.docker.internal")]
    public async Task DiscoverLocal_AddsReachableComfyUiAndIsIdempotent(string reachableHost)
    {
        var behavior = new AdapterBehavior((type, profile, _, _) => Task.FromResult(
            type == GenAiProviderType.ComfyUiLocal &&
            string.Equals(new Uri(profile.BaseUrl).Host, reachableHost, StringComparison.OrdinalIgnoreCase)
                ? Success("Connected to ComfyUI.")
                : Failure()));
        await using var factory = CreateFactory(behavior);
        var client = factory.CreateClient();

        var firstResponse = await client.PostAsync("/api/genai-provider-profiles/discover-local", null);
        var first = await firstResponse.Content.ReadFromJsonAsync<LocalGenAiProviderDiscoveryResponse>();
        var requestsAfterFirstScan = behavior.Requests.Count;
        var secondResponse = await client.PostAsync("/api/genai-provider-profiles/discover-local", null);
        var second = await secondResponse.Content.ReadFromJsonAsync<LocalGenAiProviderDiscoveryResponse>();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Single(first!.Profiles);
        Assert.Equal(LocalGenAiProviderDiscoveryStatuses.Added, Assert.Single(first.Results).Status);
        Assert.Equal(reachableHost, new Uri(first.Profiles[0].BaseUrl).Host);
        Assert.NotNull(first.Profiles[0].LastSuccessfulConnectionAt);
        Assert.Contains(behavior.Requests, request => new Uri(request.BaseUrl).Host == "127.0.0.1");
        Assert.Contains(behavior.Requests, request => new Uri(request.BaseUrl).Host == "localhost");
        Assert.Contains(behavior.Requests, request => new Uri(request.BaseUrl).Host == "host.docker.internal");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Single(second!.Profiles);
        Assert.Equal(LocalGenAiProviderDiscoveryStatuses.AlreadyConfigured, Assert.Single(second.Results).Status);
        Assert.Equal(requestsAfterFirstScan, behavior.Requests.Count);
    }

    [Fact]
    public async Task DiscoverLocal_DoesNotSaveUnreachableEndpoint()
    {
        var behavior = new AdapterBehavior((_, _, _, _) => Task.FromResult(Failure()));
        await using var factory = CreateFactory(behavior);
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/genai-provider-profiles/discover-local", null);
        var result = await response.Content.ReadFromJsonAsync<LocalGenAiProviderDiscoveryResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(result!.Profiles);
        Assert.Equal(LocalGenAiProviderDiscoveryStatuses.NotFound, Assert.Single(result.Results).Status);
    }

    private static WebApplicationFactory<Program> CreateFactory(AdapterBehavior? behavior = null)
    {
        var databaseName = Guid.NewGuid().ToString();
        behavior ??= new AdapterBehavior();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<IGenAiProviderAdapter>();
                foreach (var providerType in Enum.GetValues<GenAiProviderType>())
                {
                    var type = providerType;
                    services.AddScoped<IGenAiProviderAdapter>(_ => new StubAdapter(type, behavior));
                }
            });
        });
    }

    private static GenAiConnectionTestResponse Success(string message = "Connected.") =>
        new(true, null, message, DateTimeOffset.UtcNow);

    private static GenAiConnectionTestResponse Failure() =>
        new(false, "provider_unreachable", "Could not connect to provider.", DateTimeOffset.UtcNow);

    private sealed class AdapterBehavior
    {
        private readonly Func<GenAiProviderType, GenAiProviderProfile, string?, CancellationToken, Task<GenAiConnectionTestResponse>> _test;

        public AdapterBehavior(
            Func<GenAiProviderType, GenAiProviderProfile, string?, CancellationToken, Task<GenAiConnectionTestResponse>>? test = null)
        {
            _test = test ?? ((_, _, _, _) => Task.FromResult(Success()));
        }

        public ConcurrentBag<(GenAiProviderType ProviderType, string BaseUrl, string? ApiKey)> Requests { get; } = [];

        public Task<GenAiConnectionTestResponse> TestAsync(
            GenAiProviderType providerType,
            GenAiProviderProfile profile,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            Requests.Add((providerType, profile.BaseUrl, apiKey));
            return _test(providerType, profile, apiKey, cancellationToken);
        }
    }

    private sealed class StubAdapter(GenAiProviderType providerType, AdapterBehavior behavior) : IGenAiProviderAdapter
    {
        public GenAiProviderType ProviderType => providerType;
        public Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken) =>
            behavior.TestAsync(ProviderType, profile, apiKey, cancellationToken);
        public Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation,
            GenAiMediaRequest request, IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation,
            string providerJobId, string? apiKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
