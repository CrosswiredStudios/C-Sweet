using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Agents;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Agents;

/// <summary>Reads only the selected catalog source. Browsing never imports or approves a package.</summary>
public sealed class AgentCatalogProfileService(
    IAgentCatalogService catalog,
    CSweetDbContext db,
    LocalDirectoryAgentCatalogProvider local,
    IGitHubAgentRepositoryClient repositories,
    IPluginManifestReader manifestReader) : IAgentCatalogProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentCatalogProfileResponse?> GetAsync(
        Guid? organizationId, string agentReference, CancellationToken cancellationToken = default)
    {
        // The local provider validates the content-addressed reference while reading.
        // Avoid scanning and hashing the local workspace twice for one profile.
        if (agentReference.StartsWith("local:", StringComparison.Ordinal))
        {
            var localManifest = await local.ReadProfileManifestAsync(agentReference, cancellationToken);
            return localManifest is null ? null : Project(agentReference, localManifest);
        }
        var agent = await catalog.ResolveAsync(organizationId, agentReference, cancellationToken);
        if (agent is null) return null;

        PluginManifest? manifest;
        if (agent.Source == AgentCatalogSource.Installed)
        {
            if (!organizationId.HasValue || !agent.InstallationId.HasValue) return null;
            var businessId = organizationId.Value.ToString("D");
            var json = await db.AgentInstallations.AsNoTracking()
                .Where(x => x.Id == agent.InstallationId && x.BusinessId == businessId &&
                    x.RevisionStatus == PluginRevisionStatus.Active && x.PackageVersion != null &&
                    x.PackageVersion.PluginKind == PluginKind.Agent)
                .Select(x => x.PackageVersion!.ManifestJson)
                .SingleOrDefaultAsync(cancellationToken);
            manifest = json is null ? null : JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(agent.RepositoryUrl)) return null;
            var repository = GitHubRepositoryUrlNormalizer.Normalize(agent.RepositoryUrl);
            var branch = await repositories.GetDefaultBranchAsync(repository.Owner, repository.Name, cancellationToken);
            var commit = await repositories.ResolveCommitShaAsync(repository.Owner, repository.Name, branch, cancellationToken);
            var source = await repositories.GetRootPluginManifestAsync(repository.Owner, repository.Name, commit, cancellationToken);
            var envelope = manifestReader.Read(source.Content, source.FileName);
            if (envelope.Kind != "agent") return null;
            manifest = JsonSerializer.Deserialize<PluginManifest>(envelope.ManifestJson, JsonOptions);
            if (manifest is not null && !string.IsNullOrWhiteSpace(agent.AgentId) && manifest.Id != agent.AgentId)
                throw new AgentImportPreviewException("The repository manifest does not match this catalog agent.");
        }

        return manifest is null ? null : Project(agentReference, manifest);
    }

    private static AgentCatalogProfileResponse Project(string agentReference, PluginManifest manifest) => new(
            agentReference, manifest.Version, manifest.Provides, manifest.Requires,
            manifest.Events.Subscribes, manifest.Events.Publishes, manifest.WebAccess,
            manifest.Credentials, manifest.Connections);
}
