using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Resolves guest identities from connected fleet inventory, never from the app host.</summary>
public sealed class FleetGuestImageRegistry(CSweetDbContext dbContext, TimeProvider timeProvider)
    : IGuestImageRegistry
{
    public async Task<GuestImageReference> ResolveAsync(
        GuestImageResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        var staleAt = now.AddSeconds(-30);
        var expectedDigest = string.IsNullOrWhiteSpace(request.ExpectedDigest)
            ? null
            : NormalizeDigest(request.ExpectedDigest);

        var candidates = await dbContext.ExecutionNodeProviders.AsNoTracking()
            .Include(x => x.ExecutionNode)
            .Where(x => x.IsAvailable && x.ExecutionNode != null &&
                (!request.RequirePublicInternet || x.SupportsPublicInternet) &&
                (request.EnvironmentProfile != "software-development-polyglot-v1" || x.SupportsPublicInternet &&
                    (x.CertificationSuiteVersion == "csweet-hardware-vm-smoke-v16" || x.CertificationSuiteVersion == "csweet-hardware-vm-smoke-v16-ipv4")) &&
                x.ExecutionNode.Status == Domain.Setup.ExecutionNodeStatus.Ready &&
                x.ExecutionNode.ApprovedAt != null && x.ExecutionNode.DrainingAt == null &&
                x.ExecutionNode.RevokedAt == null &&
                x.ExecutionNode.LastHeartbeatAt >= staleAt &&
                x.ExecutionNode.CertificateExpiresAt > now &&
                (x.BrokerProtocolVersion == request.BrokerProtocolVersion || x.BrokerProtocolVersion == "1.1" && request.BrokerProtocolVersion == "1.0") &&
                x.ExecutionNode.Architecture == request.Architecture &&
                (x.CertificationExpiresAt == null || x.CertificationExpiresAt > now))
            .OrderBy(x => x.ExecutionNodeId)
            .ThenBy(x => x.ProviderId)
            .ToListAsync(cancellationToken);

        var provider = candidates.FirstOrDefault(x =>
            (expectedDigest is null || string.Equals(x.GuestImageDigest, expectedDigest, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(request.PreferredProviderId) ||
                string.Equals(x.ProviderId, request.PreferredProviderId, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(request.RequiredCertificationSuiteVersion) ||
                (string.Equals(x.CertificationSuiteVersion, request.RequiredCertificationSuiteVersion, StringComparison.Ordinal) ||
                 request.RequiredCertificationSuiteVersion == "csweet-hardware-vm-smoke-v14" &&
                    x.CertificationSuiteVersion is "csweet-hardware-vm-smoke-v15" or "csweet-hardware-vm-smoke-v15-ipv4" or "csweet-hardware-vm-smoke-v16" or "csweet-hardware-vm-smoke-v16-ipv4" && x.SupportsPublicInternet)))
            ?? throw new IsolationUnavailableException(
                "No connected execution node advertises the required certified guest image variant.");

        return new GuestImageReference(
            Required(request.LogicalImageId, nameof(request.LogicalImageId)),
            string.IsNullOrWhiteSpace(request.Version)
                ? provider.CertificationSuiteVersion
                : request.Version.Trim(),
            NormalizeDigest(provider.GuestImageDigest),
            Required(request.OperatingSystem, nameof(request.OperatingSystem)),
            Required(request.Architecture, nameof(request.Architecture)));
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value.Trim();

    private static string NormalizeDigest(string value)
    {
        var digest = value.StartsWith("sha256:", StringComparison.Ordinal) ? value : $"sha256:{value}";
        if (digest.Length != 71 || digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new IsolationUnavailableException("The guest image identity must be a lowercase SHA-256 digest.");
        return digest;
    }
}
