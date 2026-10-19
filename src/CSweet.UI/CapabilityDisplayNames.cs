using CSweet.UI.Localization;

namespace CSweet.UI;

public static class CapabilityDisplayNames
{
    /// <summary>Resource key (Agents area) for a capability with a display name, or null when the raw identifier is shown.</summary>
    public static string? KeyFor(string capability) =>
        capability switch
        {
            CSweet.Agent.SDK.RuntimePermissions.PublicInternet => "Capability.PublicInternet",
            CSweet.Contracts.Compute.ComputePermissions.LocalTestLinks => "Capability.LocalTestLinks",
            _ => null
        };

    /// <summary>Localized display name, falling back to the capability identifier.</summary>
    public static string For(IAppLocalizer localizer, string capability) =>
        KeyFor(capability) is { } key ? localizer[key] : capability;

    /// <summary>English display name. Prefer <see cref="For(IAppLocalizer, string)"/> in components.</summary>
    public static string For(string capability) =>
        capability switch
        {
            CSweet.Agent.SDK.RuntimePermissions.PublicInternet => "Public internet access",
            CSweet.Contracts.Compute.ComputePermissions.LocalTestLinks => "Share local test links",
            _ => capability
        };
}
