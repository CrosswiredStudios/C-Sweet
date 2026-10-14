using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Compute;

public static class ComputeLocalLinkGrants
{
    public static readonly string[] Actions = [InfrastructureActions.Inbound, InfrastructureActions.PublishPort];
    public static Guid GrantId(Guid environment, string action) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"explicit-local-link:{environment:N}:{action}")).AsSpan(0, 16));

    public static bool IsApproved(AgentInstallation installation)
    {
        if (!installation.IsEnabled || installation.RevisionStatus != PluginRevisionStatus.Active ||
            installation.SetupState != PluginSetupState.Ready || installation.PackageVersion is null) return false;
        var approved = JsonSerializer.Deserialize<string[]>(installation.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (!approved.Contains(ComputePermissions.LocalTestLinks) || !Actions.All(approved.Contains) ||
            string.IsNullOrWhiteSpace(installation.PackageVersion.ManifestJson)) return false;
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion.ManifestJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return manifest?.Requires.Any(x => x.Name == ComputePermissions.LocalTestLinks) == true;
        }
        catch (JsonException) { return false; }
    }

    public static bool IsHiringGrant(ScopedActionGrant grant) => grant.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity &&
        grant.GrantedBySubjectId == grant.SubjectId && Actions.Contains(grant.Action);
}
