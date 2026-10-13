namespace CSweet.UI;

public static class CapabilityDisplayNames
{
    public static string For(string capability) =>
        capability == CSweet.Agent.SDK.RuntimePermissions.PublicInternet ? "Public internet access" : capability;
}
