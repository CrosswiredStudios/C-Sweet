using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public class LlmProviderEndpointTests
{
    [Fact]
    public async Task ProviderProfile_CanBeCreatedListedAndFetched()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var created = await CreateProfileAsync(client);
        var list = await client.GetFromJsonAsync<IReadOnlyList<LlmProviderProfileResponse>>("/api/llm-provider-profiles");
        var fetched = await client.GetFromJsonAsync<LlmProviderProfileResponse>($"/api/llm-provider-profiles/{created.Id}");

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.DoesNotContain("secret-test-key", JsonSerializer.Serialize(created), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(list);
        Assert.Contains(list, x => x.Id == created.Id);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task ProviderProfile_CanBeUpdatedAndDeleted()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var created = await CreateProfileAsync(client);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/llm-provider-profiles/{created.Id}",
            new UpdateLlmProviderProfileRequest(
                "Updated LM Studio",
                LlmProviderType.OpenAiCompatible,
                "http://fake-provider/v2",
                "updated-key",
                ReplaceApiKey: true,
                "updated-model",
                null,
                8192,
                1024,
                SupportsStreaming: true,
                SupportsToolCalling: true,
                SupportsStructuredOutput: true,
                SupportsVision: false,
                IsEnabled: true));
        var updateResult = await updateResponse.Content.ReadFromJsonAsync<LlmProviderProfileActionResponse>();

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updateResult?.Profile);
        Assert.Equal("Updated LM Studio", updateResult.Profile.Name);
        Assert.Equal("updated-model", updateResult.Profile.DefaultChatModel);

        var deleteResponse = await client.DeleteAsync($"/api/llm-provider-profiles/{created.Id}");
        var fetchedAfterDelete = await client.GetAsync($"/api/llm-provider-profiles/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, fetchedAfterDelete.StatusCode);
    }

    [Theory]
    [InlineData(LlmProviderType.LmStudio)]
    [InlineData(LlmProviderType.UnslothStudio)]
    [InlineData(LlmProviderType.Ollama)]
    [InlineData(LlmProviderType.Vllm)]
    [InlineData(LlmProviderType.OpenAi)]
    [InlineData(LlmProviderType.GoogleGemini)]
    [InlineData(LlmProviderType.OpenRouter)]
    [InlineData(LlmProviderType.Groq)]
    [InlineData(LlmProviderType.TogetherAi)]
    [InlineData(LlmProviderType.Custom)]
    public async Task PreviewModelCatalog_ReturnsAvailableModelsForSupportedSetupProviders(LlmProviderType providerType)
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/llm-provider-profiles/model-catalog/preview",
            new PreviewModelCatalogRequest(
                providerType,
                "http://fake-provider/v1",
                "secret-test-key"));
        var result = await response.Content.ReadFromJsonAsync<PreviewModelCatalogResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Contains(result.Models, model => model.Id == "local-model");
    }

    [Fact]
    public async Task SavedProviderModelCatalog_ReturnsAvailableModels()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var created = await CreateProfileAsync(client);

        var response = await client.GetAsync($"/api/llm-provider-profiles/{created.Id}/models");
        var result = await response.Content.ReadFromJsonAsync<PreviewModelCatalogResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Contains(result.Models, model => model.Id == "local-model");
    }

    [Fact]
    public async Task TestProvider_PersistsCapabilityTestAndUpdatesLastSuccessfulConnection()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var created = await CreateProfileAsync(client);

        var response = await client.PostAsync($"/api/llm-provider-profiles/{created.Id}/test", content: null);
        var result = await response.Content.ReadFromJsonAsync<ModelCapabilityTestResult>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.True(result.ConnectionSucceeded);
        Assert.True(result.ChatSucceeded);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var persistedTest = await dbContext.ModelCapabilityTests.SingleAsync(x => x.ProviderProfileId == created.Id);
        var profile = await dbContext.LlmProviderProfiles.SingleAsync(x => x.Id == created.Id);

        Assert.True(persistedTest.ChatSucceeded);
        Assert.NotNull(profile.LastSuccessfulConnectionAt);
    }

    [Fact]
    public async Task SetDefaultChatProvider_SucceedsOnlyAfterSuccessfulChatTest()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var created = await CreateProfileAsync(client);

        var beforeTestResponse = await client.PostAsJsonAsync(
            "/api/setup/default-chat-provider",
            new SetDefaultChatProviderRequest(created.Id));

        Assert.Equal(HttpStatusCode.BadRequest, beforeTestResponse.StatusCode);

        await client.PostAsync($"/api/llm-provider-profiles/{created.Id}/test", content: null);

        var afterTestResponse = await client.PostAsJsonAsync(
            "/api/setup/default-chat-provider",
            new SetDefaultChatProviderRequest(created.Id));

        Assert.Equal(HttpStatusCode.OK, afterTestResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var configuration = await dbContext.SystemConfigurations.SingleAsync();

        Assert.Equal(created.Id, configuration.DefaultChatProviderId);
    }

    [Fact]
    public async Task DiscoverLocal_AddsReachableProvidersAndIsIdempotent()
    {
        var handler = new SelectiveDiscoveryHandler();
        await using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var firstResponse = await client.PostAsync("/api/llm-provider-profiles/discover-local", content: null);
        var first = await firstResponse.Content.ReadFromJsonAsync<LocalLlmProviderDiscoveryResponse>();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.NotNull(first);
        Assert.Equal(2, first.Profiles.Count);
        Assert.Equal(2, first.Results.Count(result => result.Status == LocalLlmProviderDiscoveryStatuses.Added));
        Assert.Contains(first.Profiles, profile =>
            profile.ProviderType == LlmProviderType.LmStudio &&
            profile.DefaultChatModel == string.Empty);
        Assert.Contains(first.Profiles, profile => profile.ProviderType == LlmProviderType.Ollama);
        Assert.Contains(first.Results, result =>
            result.ProviderType == LlmProviderType.UnslothStudio &&
            result.Status == LocalLlmProviderDiscoveryStatuses.NotFound);
        Assert.Contains(first.Results, result =>
            result.ProviderType == LlmProviderType.Vllm &&
            result.Status == LocalLlmProviderDiscoveryStatuses.NotFound);
        Assert.Equal(8, handler.Requests.Count);
        Assert.Contains(handler.Requests, uri => uri.Host == "localhost" && uri.Port == 1234);
        Assert.Contains(handler.Requests, uri => uri.Host == "host.docker.internal" && uri.Port == 1234);

        var secondResponse = await client.PostAsync("/api/llm-provider-profiles/discover-local", content: null);
        var second = await secondResponse.Content.ReadFromJsonAsync<LocalLlmProviderDiscoveryResponse>();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.NotNull(second);
        Assert.Equal(2, second.Profiles.Count);
        Assert.DoesNotContain(second.Results, result => result.Status == LocalLlmProviderDiscoveryStatuses.Added);
        Assert.Equal(2, second.Results.Count(result =>
            result.Status == LocalLlmProviderDiscoveryStatuses.AlreadyConfigured));
    }

    [Fact]
    public async Task DiscoverLocal_DoesNotSaveEmptyModelCatalogs()
    {
        await using var factory = CreateFactory(new EmptyCatalogHandler());
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/llm-provider-profiles/discover-local", content: null);
        var result = await response.Content.ReadFromJsonAsync<LocalLlmProviderDiscoveryResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Empty(result.Profiles);
        Assert.All(result.Results, item =>
            Assert.Equal(LocalLlmProviderDiscoveryStatuses.NotFound, item.Status));
    }

    private static async Task<LlmProviderProfileResponse> CreateProfileAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/llm-provider-profiles",
            new CreateLlmProviderProfileRequest(
                "Local LM Studio",
                LlmProviderType.LmStudio,
                "http://fake-provider/v1",
                "secret-test-key",
                "local-model",
                null,
                null,
                null,
                SupportsStreaming: true,
                SupportsToolCalling: false,
                SupportsStructuredOutput: false,
                SupportsVision: false));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LlmProviderProfileResponse>())!;
    }

    private static WebApplicationFactory<Program> CreateFactory(HttpMessageHandler? providerHandler = null)
    {
        var databaseName = Guid.NewGuid().ToString();
        providerHandler ??= new FakeProviderHandler();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                    services.AddDbContext<CSweetDbContext>(options =>
                        options.UseInMemoryDatabase(databaseName));

                    services.RemoveAll<OpenAiCompatibleProviderClient>();
                    services.AddScoped(_ => new OpenAiCompatibleProviderClient(new HttpClient(providerHandler)
                    {
                        Timeout = TimeSpan.FromSeconds(5)
                    }));
                });
            });
    }

    private sealed class SelectiveDiscoveryHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                Requests.Add(request.RequestUri);
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase) != true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return request.RequestUri.Port switch
            {
                1234 or 11434 => Task.FromResult(JsonResponse(new
                {
                    data = new[] { new { id = "local-model", owned_by = "local-runtime" } }
                })),
                8888 => Task.FromResult(JsonResponse(new { data = Array.Empty<object>() })),
                8000 => TimeoutAsync(cancellationToken),
                _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("Provider unavailable."))
            };
        }

        private static async Task<HttpResponseMessage> TimeoutAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return JsonResponse(new { data = Array.Empty<object>() });
        }
    }

    private sealed class EmptyCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(JsonResponse(new { data = Array.Empty<object>() }));
        }
    }

    private sealed class FakeProviderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(JsonResponse(new
                {
                    data = new[] { new { id = "local-model", owned_by = "fake-provider" } }
                }));
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(JsonResponse(new
                {
                    choices = new[]
                    {
                        new { message = new { role = "assistant", content = "READY" } }
                    }
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

    }

    private static HttpResponseMessage JsonResponse(object body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };
    }
}
