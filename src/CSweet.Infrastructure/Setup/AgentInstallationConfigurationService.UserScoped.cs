using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentInstallationConfigurationService
{
    private async Task<AgentInstallationConfigurationSnapshot> SaveUserScopedAsync(Guid id, string schemaVersion,
        IReadOnlyDictionary<string, JsonElement> values, CancellationToken token)
    {
        var installation = await db.AgentInstallations.Include(x => x.PackageVersion).Include(x => x.Configuration)
            .Include(x => x.RuntimeInstances).Include(x => x.Schedule).SingleAsync(x => x.Id == id, token);
        var manifest = AgentConfigurationRules.DeserializeManifest(installation.PackageVersion!.ManifestJson);
        var settings = AgentTokenBudget.WithDefaults(manifest, values);
        await AgentConfigurationRules.ValidateAsync(db, manifest, settings, requireRequired: true,
            token, modelCatalog, validateSupportedModels: true);
        var configuration = installation.Configuration!;
        var previous = Deserialize(configuration.SettingsJson);
        if (!SettingsEqual(previous, settings))
        {
            configuration.SettingsJson = Serialize(settings);
            configuration.SchemaVersion = schemaVersion;
            configuration.Revision++;
            configuration.UpdatedAt = DateTimeOffset.UtcNow;
            MarkConfigurationChanged(installation);
            await SaveWithRevisionConflictAsync(configuration, token);
            await auditWriter.WriteAsync("agent-installation.configuration.updated", nameof(AgentInstallation), id,
                $"Updated user-scoped assistant configuration to revision {configuration.Revision}.", cancellationToken: token);
            if (HasActiveRuntime(installation)) await QueueRefreshAsync(id, ChangedKeys(previous, settings), token);
        }
        return new(id, configuration.SchemaVersion, settings, configuration.CreatedAt, configuration.UpdatedAt);
    }
}
