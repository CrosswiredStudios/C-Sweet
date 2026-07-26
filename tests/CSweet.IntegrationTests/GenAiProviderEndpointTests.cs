using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<IGenAiProviderAdapter>();
                services.AddScoped<IGenAiProviderAdapter>(_ => new StubAdapter());
            });
        });
    }

    private sealed class StubAdapter : IGenAiProviderAdapter
    {
        public GenAiProviderType ProviderType => GenAiProviderType.OpenAi;
        public Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new GenAiConnectionTestResponse(true, null, "Connected.", DateTimeOffset.UtcNow));
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
