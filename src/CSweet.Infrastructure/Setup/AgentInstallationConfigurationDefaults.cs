using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using CSweet.Infrastructure.Llm;

namespace CSweet.Infrastructure.Setup;

internal static class AgentInstallationConfigurationDefaults
{
    public static async Task EnsureAsync(
        CSweetDbContext dbContext,
        AgentInstallation installation,
        CancellationToken cancellationToken)
    {
        if (await dbContext.AgentInstallationConfigurations.AnyAsync(
                x => x.AgentInstallationId == installation.Id,
                cancellationToken))
            return;

        var requestedCapabilities = JsonSerializer.Deserialize<IReadOnlyList<string>>(
            installation.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (!requestedCapabilities.Contains(PlatformCapabilities.LlmChatStream, StringComparer.Ordinal))
            return;

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(
                installation.PackageVersion?.ManifestJson ?? "{}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (JsonException)
        {
            return;
        }

        var configurationKeys = manifest?.Configuration
            .Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (!configurationKeys.Contains("llmProviderId") || !configurationKeys.Contains("llmModel"))
            return;

        var defaults = await DefaultLlmSelection.ReadAsync(dbContext, cancellationToken);
        if (defaults is null) return;

        var now = DateTimeOffset.UtcNow;
        dbContext.AgentInstallationConfigurations.Add(new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            SchemaVersion = "1.0",
            SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["llmProviderId"] = defaults.ProviderId.ToString("D"),
                ["llmModel"] = defaults.Model
            }),
            CreatedAt = now,
            UpdatedAt = now
        });
    }
}
