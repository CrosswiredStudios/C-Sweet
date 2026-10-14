namespace CSweet.UI;

public static class CapabilityDisplayNames
{
    public static string For(string capability) =>
        capability switch
        {
            CSweet.Agent.SDK.RuntimePermissions.PublicInternet => "Public internet access",
            CSweet.Contracts.Compute.ComputePermissions.LocalTestLinks => "Share local test links",
            _ => capability
        };
}
