using CSweet.Agent.SDK;

namespace CSweet.Infrastructure.Core;

/// <summary>Trusted provider implementation; agents request capabilities, never installer commands.</summary>
public interface IResourceFulfillmentHandler
{
    string Key { get; }
    bool Supports(string resourceType, string profileKey);
    Task<ResourcePlan> PrepareAsync(Guid organizationId, ResourceRequest request, CancellationToken token);
    Task ValidateAsync(Guid organizationId, ResourceRequestState request, CancellationToken token);
    Task<ResourceFulfillmentProgress> ReconcileAsync(Guid organizationId, ResourceRequestState request, CancellationToken token);
}

public sealed record ResourceFulfillmentProgress(string Status, string? Condition = null,
    Guid? ProviderInstallationId = null, Guid? CertificationRunId = null);
