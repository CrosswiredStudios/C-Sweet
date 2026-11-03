using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

// Presentation only. A completed section is never consent, a grant, or agent readiness.
public enum PluginSetupSectionKind { Requirements, Access, Status }
public sealed record PluginSetupSection(PluginSetupSectionKind Kind, string Label, bool Complete);

public static class PluginSetupPresentation
{
    public static IReadOnlyList<PluginSetupSection> ForAccounts(PluginAccountManagementView view)
    {
        var connected = view.Accounts.Where(a => a.State == "Connected" &&
            a.Authorizations.Any(x => x.State == "Connected") && a.Resources.Any(r => r.Available)).ToArray();
        List<PluginSetupSection> sections = [new(PluginSetupSectionKind.Requirements, "Requirements", connected.Length > 0)];
        if (view.CanAdminister && view.Declaration.Permissions.Count > 0)
            sections.Add(new(PluginSetupSectionKind.Access, "Business access", connected.Any(a => a.BusinessGrants.Any(g =>
                !g.Revoked && g.PermissionIds.Count > 0 && (g.Scope == "WholeAccount" ||
                    a.Resources.Any(r => r.Available && g.ResourceIds.Contains(r.Id)))))));
        sections.Add(new(PluginSetupSectionKind.Status, "Status", false));
        return sections;
    }

    public static PluginAccountSetupView? CurrentAttempt(PluginAccountManagementView view, Guid? requested) =>
        // The API returns attempts newest first. Keep the exact return target when supplied.
        requested is { } id ? view.SetupSessions.FirstOrDefault(s => s.Id == id) : view.SetupSessions.FirstOrDefault();
}
