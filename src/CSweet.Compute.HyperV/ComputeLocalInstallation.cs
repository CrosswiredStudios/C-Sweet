using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

/// <summary>Fixed, administrator-owned installation locations; never supplied by an agent.</summary>
internal sealed record ComputeLocalInstallation(Guid? BusinessId)
{
    public string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CSweet", BusinessId is { } id ? Path.Combine("ComputeBusinesses", id.ToString("N")) : "Compute");
    public string ConfigurationPath => Path.Combine(Root, "provider.json");
    public string ServiceName => ComputeWindowsService.ServiceName + (BusinessId is { } id ? "." + id.ToString("N") : "");

    internal static ComputeLocalInstallation Select(Guid organizationId, Guid? legacyOrganizationId, bool businessInstalled)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("A business identity is required.", nameof(organizationId));
        // Existing scoped installations always win, including after a legacy installation is removed.
        return new(businessInstalled || legacyOrganizationId is { } legacy && legacy != organizationId ? organizationId : null);
    }

    internal static async Task<ComputeLocalInstallation> ResolveAsync(Guid organizationId, CancellationToken token)
    {
        var business = new ComputeLocalInstallation(organizationId);
        if (File.Exists(business.ConfigurationPath)) return business;
        var legacy = new ComputeLocalInstallation(BusinessId: null);
        var configuration = File.Exists(legacy.ConfigurationPath)
            ? await ComputeProviderConfigurationLoader.ReadAsync(legacy.ConfigurationPath, token) : null;
        return Select(organizationId, configuration?.Enrollment.OrganizationId, false);
    }
}
