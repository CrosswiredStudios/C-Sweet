namespace CSweet.Domain.Core;

/// <summary>Platform fixture evidence shares build storage, never business project ownership.</summary>
public static class WorkstreamPurpose
{
    public const string ToolchainCertificationProfile = "platform.toolchain-certification.v1";

    public static IQueryable<Workstream> BusinessProjects(this IQueryable<Workstream> query) =>
        query.Where(x => x.ProfileKey == null || x.ProfileKey != ToolchainCertificationProfile);
}
