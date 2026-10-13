using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public static class RuntimeInternetGrant
{
    public static bool IsGranted(string manifestJson, AgentInstallationGrant? grant)
    {
        if (grant is null) return false;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, options);
        return manifest?.Requires.Any(x => x.Name == RuntimePermissions.PublicInternet) == true &&
            (JsonSerializer.Deserialize<string[]>(grant.RequiredCapabilitiesJson) ?? []).Contains(RuntimePermissions.PublicInternet, StringComparer.Ordinal);
    }

    public static async Task<bool> IsCurrentAsync(CSweetDbContext db, string specificationJson, CancellationToken token)
    {
        using var json = JsonDocument.Parse(specificationJson);
        if (!(json.RootElement.TryGetProperty("NetworkPolicy", out var network) || json.RootElement.TryGetProperty("networkPolicy", out network)) ||
            network.ValueKind == JsonValueKind.Null) return true;
        if (!(json.RootElement.TryGetProperty("Kind", out var kind) || json.RootElement.TryGetProperty("kind", out kind)) || kind.GetInt32() != (int)WorkloadKind.Runtime) return false;
        var workload = JsonSerializer.Deserialize<RuntimeWorkloadSpecification>(specificationJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (workload?.Kind != WorkloadKind.Runtime || workload.NetworkPolicy is null) return false;
        workload.NetworkPolicy.Validate();
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == workload.Identity.InstallationId && x.BusinessId == workload.Identity.BusinessId, token);
        return installation is { IsEnabled: true, RevisionStatus: PluginRevisionStatus.Active, Grant: not null, PackageVersion: not null } &&
            installation.Grant.GrantRevision == workload.NetworkPolicy.GrantRevision && IsGranted(installation.PackageVersion.ManifestJson, installation.Grant);
    }
}
