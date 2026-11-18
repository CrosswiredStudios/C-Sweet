using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.GenAi;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.UnitTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.GameRoleWorkflow.Tests;

// Registers an ephemeral installation and runs its unmodified SDK request through the app's broker.
internal sealed class CheckpointModelEndpoint(string model, AgentSession session, PlatformLlmCapabilityHandler handler)
{
    public string Model => model;

    public static async Task<CheckpointModelEndpoint> OpenAsync(CSweetDbContext db, AgentCoordinationParticipant actor,
        Guid organization, Guid providerId)
    {
        var url = Environment.GetEnvironmentVariable("CSWEET_STACK_CHECKPOINT_URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Supply an HTTP(S) API base URL without credentials, query or fragment.");
        var model = Environment.GetEnvironmentVariable("CSWEET_STACK_CHECKPOINT_MODEL");
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Select a model ID.");
        var budgetText = Environment.GetEnvironmentVariable("CSWEET_STACK_CHECKPOINT_OUTPUT_TOKENS");
        if (!int.TryParse(budgetText, out var budget) || budget < 1 || budget >= AgentTokenBudget.DefaultContextTokens)
            throw new InvalidOperationException($"Select an output budget between 1 and {AgentTokenBudget.DefaultContextTokens - 1}.");

        var secrets = new InMemoryLlmProviderSecretStore();
        var profile = new LlmProviderProfile { Id = providerId, Name = "Checkpoint", ProviderType = LlmProviderType.OpenAiCompatible,
            BaseUrl = endpoint.AbsoluteUri, DefaultChatModel = model, IsEnabled = true, SupportsStreaming = true, SupportsStructuredOutput = true };
        if (Environment.GetEnvironmentVariable("CSWEET_STACK_CHECKPOINT_KEY_VARIABLE") is { Length: > 0 } variable)
        {
            var key = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Selected API key environment variable is empty.");
            profile.ApiKeySecretName = "checkpoint-key";
            await secrets.StoreAsync(profile.ApiKeySecretName, key);
        }

        var installation = await db.AgentInstallations.Include(i => i.Grant).Include(i => i.PackageVersion)
            .SingleAsync(i => i.Id == actor.AgentInstallationId);
        var package = installation.PackageVersion ?? throw new InvalidOperationException("Missing checkpoint agent package.");
        using var manifest = JsonDocument.Parse(package.ManifestJson);
        var required = manifest.RootElement.GetProperty("requires").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToHashSet();
        Assert.Contains(PlatformCapabilities.LlmChatStream, required);
        installation.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(required);
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installation.Id, QueuedAt = now };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now);
        runtime.TransitionTo(AgentRuntimeStatus.Running, now);
        db.AddRange(runtime, profile, new AgentInstallationConfiguration { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            SettingsJson = JsonSerializer.Serialize(new { llmProviderId = providerId, llmModel = model, maxOutputTokens = budget }) });
        await db.SaveChangesAsync();

        var session = new AgentSession("checkpoint", package.AgentId, installation.Id.ToString(), organization.ToString(),
            runtime.Id.ToString(), runtime.TickId.ToString(), new(new HashSet<string>(), new HashSet<string>(), required, installation.Grant.GrantRevision));
        var factory = new OpenAiCompatibleLlmProviderFactory(db, secrets, NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance);
        var assets = new MediaAssetService(db, new FileMediaAssetStore(new ConfigurationBuilder().Build(), Options.Create(new MediaAssetStorageOptions())));
        var handler = new PlatformLlmCapabilityHandler(db, factory, new AgentEmployeeIdentityResolver(db),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], assets, NullLogger<PlatformLlmCapabilityHandler>.Instance);
        return new(model, session, handler);
    }

    public async Task<string> GenerateTextAsync(JsonElement payload, CancellationToken token)
    {
        Assert.Equal(Model, payload.GetProperty("model").GetString());
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString(), Capability = PlatformCapabilities.LlmChatStream,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload)) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var text = new StringBuilder();
        await foreach (var chunk in handler.StreamAsync(session, request, timeout.Token))
        {
            Assert.True(chunk.Succeeded, $"{chunk.FailureCode}: {chunk.Error}");
            var json = JsonSerializer.Deserialize<JsonElement>(chunk.Payload.Span);
            if (json.TryGetProperty("text", out var delta)) text.Append(delta.GetString());
        }
        return text.ToString();
    }
}
