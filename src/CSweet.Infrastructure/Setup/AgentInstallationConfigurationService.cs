using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentInstallationConfigurationService(
    CSweetDbContext dbContext,
    IAuditEventWriter auditWriter) : IAgentInstallationConfigurationService
{
    private const int MaximumSettingsBytes = 256 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentInstallationConfigurationSnapshot?> GetAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var configuration = await dbContext.AgentInstallationConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.AgentInstallationId == installationId, cancellationToken);

        return configuration is null ? null : ToSnapshot(configuration);
    }

    public async Task<AgentInstallationConfigurationSnapshot> SaveAsync(
        Guid installationId,
        string schemaVersion,
        IReadOnlyDictionary<string, JsonElement> settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion) || schemaVersion.Length > 64)
        {
            throw new AgentInstallationException("Agent configuration schema version is required and cannot exceed 64 characters.");
        }

        if (settings.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new AgentInstallationException("Agent configuration keys cannot be empty.");
        }

        var installation = await dbContext.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new AgentInstallationException("The agent installation was not found.");
        var manifest = DeserializeManifest(installation.PackageVersion?.ManifestJson);
        await ValidateSettingsAsync(manifest, settings, cancellationToken);

        var settingsJson = JsonSerializer.Serialize(settings, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(settingsJson) > MaximumSettingsBytes)
        {
            throw new AgentInstallationException($"Agent configuration cannot exceed {MaximumSettingsBytes / 1024} KB.");
        }

        var now = DateTimeOffset.UtcNow;
        var configuration = await dbContext.AgentInstallationConfigurations
            .SingleOrDefaultAsync(x => x.AgentInstallationId == installationId, cancellationToken);
        if (configuration is null)
        {
            configuration = new AgentInstallationConfiguration
            {
                Id = Guid.NewGuid(),
                AgentInstallationId = installationId,
                CreatedAt = now
            };
            dbContext.AgentInstallationConfigurations.Add(configuration);
        }

        configuration.SchemaVersion = schemaVersion.Trim();
        configuration.SettingsJson = settingsJson;
        configuration.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            "agent-installation.configuration.updated",
            nameof(AgentInstallation),
            installationId,
            "Updated persisted agent installation configuration.",
            cancellationToken: cancellationToken);

        return ToSnapshot(configuration);
    }

    private static AgentInstallationConfigurationSnapshot ToSnapshot(
        AgentInstallationConfiguration configuration) =>
        new(
            configuration.AgentInstallationId,
            configuration.SchemaVersion,
            JsonSerializer.Deserialize<IReadOnlyDictionary<string, JsonElement>>(
                configuration.SettingsJson,
                SerializerOptions) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            configuration.CreatedAt,
            configuration.UpdatedAt);

    private async Task ValidateSettingsAsync(
        PluginManifest manifest,
        IReadOnlyDictionary<string, JsonElement> settings,
        CancellationToken cancellationToken)
    {
        var fields = manifest.Configuration
            .Where(field => !field.Secret)
            .ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var configField in fields.Values)
        {
            var hasValue = settings.TryGetValue(configField.Key, out var value) &&
                           value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
                           (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));
            if (configField.Required && !hasValue)
                throw new AgentInstallationException(
                    $"'{configField.Label}' ({configField.Key}) is required.");
            if (!hasValue)
                continue;

            if (configField.Type.Equals("boolean", StringComparison.OrdinalIgnoreCase) &&
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AgentInstallationException($"'{configField.Label}' must be true or false.");
            if (configField.Type.Equals("number", StringComparison.OrdinalIgnoreCase) &&
                value.ValueKind != JsonValueKind.Number)
                throw new AgentInstallationException($"'{configField.Label}' must be a number.");
            if (configField.Type.Equals(AgentConfigurationFieldTypes.Select, StringComparison.OrdinalIgnoreCase) &&
                configField.Options is { Count: > 0 } &&
                (value.ValueKind != JsonValueKind.String ||
                 !configField.Options.Any(option =>
                     string.Equals(option.Value, value.GetString(), StringComparison.Ordinal))))
            {
                throw new AgentInstallationException(
                    $"'{configField.Label}' must be one of the values declared by the agent.");
            }
            if ((configField.Type.Equals("provider", StringComparison.OrdinalIgnoreCase) ||
                 configField.Type.Equals("llmProvider", StringComparison.OrdinalIgnoreCase)) &&
                (value.ValueKind != JsonValueKind.String ||
                 !Guid.TryParse(value.GetString(), out var providerId) ||
                 !await dbContext.LlmProviderProfiles.AsNoTracking().AnyAsync(
                     provider => provider.Id == providerId && provider.IsEnabled,
                     cancellationToken)))
                throw new AgentInstallationException(
                    $"'{configField.Label}' must reference an enabled LLM provider.");
        }
    }

    private static PluginManifest DeserializeManifest(string? manifestJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(manifestJson ?? "{}", SerializerOptions)
                   ?? throw new AgentInstallationException("The stored plugin manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentInstallationException(
                $"The stored plugin manifest is invalid: {exception.Message}");
        }
    }
}
