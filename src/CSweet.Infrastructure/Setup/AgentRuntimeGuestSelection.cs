using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.Workloads;

namespace CSweet.Infrastructure.Setup;

// Eligibility and launch must resolve the same image, including configured digest pins.
internal static class AgentRuntimeGuestSelection
{
    internal static (string? EnvironmentProfile, string WorkspaceAccess) ReadRequest(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("runtime", out var runtime)) return (null, "None");
        return (runtime.TryGetProperty("environmentProfile", out var profile) ? profile.GetString() : null,
            runtime.TryGetProperty("workspaceAccess", out var access) ? access.GetString() ?? "None" : "None");
    }

    internal static async Task<(GuestImageReference Image, DevelopmentGuestImageOptions? DevelopmentImage)> ResolveAsync(
        AgentPackageVersion package, AgentInstallationGrant? grant, AgentRuntimeManagerOptions options,
        IGuestImageRegistry registry, CancellationToken token)
    {
        var request = ReadRequest(package.ManifestJson);
        var developer = request.EnvironmentProfile == "software-development-polyglot-v1";
        if (developer && request.WorkspaceAccess != "ReadWrite")
            throw new IsolationUnavailableException("The software development runtime requires ReadWrite workspace access.");
        var internet = RuntimeInternetGrant.IsGranted(package.ManifestJson, grant);
        var protocol = internet || developer ? "1.1" : "1.0";
        if (developer)
        {
            foreach (var candidate in options.DevelopmentImages.Where(x => x.Architecture == package.ArtifactArchitecture &&
                !string.IsNullOrWhiteSpace(x.Digest) && (options.PreferredIsolationProviderId == null ||
                x.ProviderId == options.PreferredIsolationProviderId)).OrderBy(x => x.ProviderId, StringComparer.Ordinal))
            {
                try
                {
                    var image = await registry.ResolveAsync(new(candidate.ImageId, candidate.Version, "linux",
                        candidate.Architecture, AgentTrustLevel.UntrustedRepository, protocol, candidate.ProviderId,
                        candidate.Digest, candidate.CertificationSuiteVersion, request.EnvironmentProfile, internet), token);
                    return (image, candidate);
                }
                catch (IsolationUnavailableException) { }
            }
            if (options.DevelopmentImages.Count == 0)
                return (await registry.ResolveAsync(new("csweet-development-polyglot-v1", null, "linux",
                    package.ArtifactArchitecture, AgentTrustLevel.UntrustedRepository, protocol,
                    options.PreferredIsolationProviderId, null, null, request.EnvironmentProfile, internet), token), null);
            throw new IsolationUnavailableException("No connected provider has the configured certified development image for this architecture.");
        }
        return (await registry.ResolveAsync(new(options.RuntimeGuestImageId, options.RuntimeGuestImageVersion,
            options.RuntimeGuestOperatingSystem, options.RuntimeGuestArchitecture, AgentTrustLevel.UntrustedRepository,
            protocol, options.PreferredIsolationProviderId, options.RuntimeGuestImageDigest,
            options.RequiredCertificationSuiteVersion, null, internet), token), null);
    }
}
