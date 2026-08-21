using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Connects already-approved requester grants to a uniquely eligible provider without knowing
/// anything about agent roles or business workflows. Ambiguous provider sets remain unbound.
/// </summary>
internal sealed class AgentCapabilityBindingReconciler(
    CSweetDbContext db,
    IAuditEventWriter auditWriter)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> ReconcileAsync(
        string? businessId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.AgentInstallations
            .Include(x => x.PackageVersion)
            .Include(x => x.Grant)
            .Include(x => x.Schedule)
            .Where(x => x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active);
        if (!string.IsNullOrWhiteSpace(businessId))
            query = query.Where(x => x.BusinessId == businessId);

        var installations = await query.AsSplitQuery().ToListAsync(cancellationToken);
        if (installations.Count == 0)
            return 0;

        var installationIds = installations.Select(x => x.Id).ToArray();
        var activeBindings = await db.AgentCapabilityBindings
            .Where(x => installationIds.Contains(x.RequesterInstallationId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var byId = installations.ToDictionary(x => x.Id);
        var provided = installations.ToDictionary(
            x => x.Id,
            x => DeserializeGrant(x.Grant?.ProvidedCapabilitiesJson)
                .Intersect(AgentConfigurationRules.DeserializeManifest(x.PackageVersion!.ManifestJson)
                    .Provides.Select(capability => capability.Name), StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal));
        var now = DateTimeOffset.UtcNow;
        var created = new List<AgentCapabilityBinding>();

        foreach (var requester in installations)
        {
            var required = DeserializeGrant(requester.Grant?.RequiredCapabilitiesJson);
            foreach (var capability in required)
            {
                var existing = activeBindings.FirstOrDefault(x =>
                    x.RequesterInstallationId == requester.Id &&
                    string.Equals(x.Capability, capability, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (byId.TryGetValue(existing.ProviderInstallationId, out var currentProvider) &&
                        currentProvider.BusinessId == requester.BusinessId &&
                        provided[currentProvider.Id].Contains(capability))
                    {
                        existing.GrantRevision = requester.Grant!.GrantRevision;
                        continue;
                    }

                    if (!string.Equals(existing.Origin, AgentCapabilityBindingOrigins.AutomaticUnique,
                            StringComparison.Ordinal))
                        continue;
                    existing.RevokedAt = now;
                }

                var providers = installations.Where(provider =>
                        provider.Id != requester.Id &&
                        provider.BusinessId == requester.BusinessId &&
                        provided[provider.Id].Contains(capability))
                    .ToList();
                if (providers.Count != 1)
                    continue;

                var binding = new AgentCapabilityBinding
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = requester.BusinessId,
                    RequesterInstallationId = requester.Id,
                    Capability = capability,
                    ProviderInstallationId = providers[0].Id,
                    GrantRevision = requester.Grant!.GrantRevision,
                    Origin = AgentCapabilityBindingOrigins.AutomaticUnique,
                    ApprovedAt = now
                };
                db.AgentCapabilityBindings.Add(binding);
                if (requester.Schedule is { IsEnabled: true })
                    requester.Schedule.NextAttentionReviewAt = now;
                activeBindings.Add(binding);
                created.Add(binding);
            }
        }

        if (created.Count == 0 && !db.ChangeTracker.HasChanges())
            return 0;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var binding in created)
        {
            await auditWriter.WriteAsync(
                "agent-capability-binding.automatic-unique",
                nameof(AgentCapabilityBinding),
                binding.Id,
                $"Bound {binding.Capability} to the sole approved in-scope provider {binding.ProviderInstallationId:D}.",
                cancellationToken: cancellationToken);
        }
        return created.Count;
    }

    private static HashSet<string> DeserializeGrant(string? json) =>
        (JsonSerializer.Deserialize<string[]>(json ?? "[]", JsonOptions) ?? [])
        .ToHashSet(StringComparer.Ordinal);
}
