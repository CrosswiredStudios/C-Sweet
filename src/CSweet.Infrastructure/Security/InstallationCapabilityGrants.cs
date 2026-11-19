using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

internal static class InstallationCapabilityGrants
{
    internal static async Task<bool> HasRequiredCapabilityAsync(this CSweetDbContext db, Guid organization,
        Guid? installation, string capability, CancellationToken token)
    {
        var business = organization.ToString("D");
        var grants = await db.AgentInstallations.AsNoTracking().Where(i => i.Id == installation && i.BusinessId == business &&
            i.IsEnabled && i.RevisionStatus == PluginRevisionStatus.Active && i.SetupState == PluginSetupState.Ready && i.Grant != null)
            .Select(i => i.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(token);
        return grants is not null && (JsonSerializer.Deserialize<string[]>(grants) ?? []).Contains(capability, StringComparer.Ordinal);
    }
}
