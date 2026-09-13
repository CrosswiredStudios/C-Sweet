using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Application.Compute;

/// <summary>Versioned policy attached to existing scoped grants; missing limits deny admission.</summary>
public sealed record ComputeGrantConstraints(
    int Version,
    ComputeResources MaximumResources,
    int MaximumConcurrentEnvironments,
    int MaximumLifetimeSeconds,
    HashSet<string> OperatingSystems,
    HashSet<string> Architectures,
    HashSet<string> Templates,
    bool AllowPersistent = false,
    bool AllowOutbound = false,
    bool AllowPublicEndpoint = false,
    HashSet<int>? AllowedPublishedPorts = null,
    Guid? EnvironmentId = null)
{
    public bool Allows(ComputeSpecification specification, int activeEnvironments) =>
        Version == 1 && MaximumResources is { IsValid: true } &&
        MaximumConcurrentEnvironments > 0 && MaximumLifetimeSeconds > 0 &&
        activeEnvironments >= 0 && activeEnvironments < MaximumConcurrentEnvironments &&
        specification.Resources.Fits(MaximumResources) && specification.LifetimeSeconds <= MaximumLifetimeSeconds &&
        OperatingSystems?.Contains(specification.OperatingSystem) == true &&
        Architectures?.Contains(specification.Architecture) == true && Templates?.Contains(specification.TemplateId) == true &&
        (specification.Persistence != ComputePersistence.Persistent || AllowPersistent) &&
        (!specification.NetworkPolicy.AllowOutbound || AllowOutbound) &&
        (!specification.NetworkPolicy.PublicEndpoint || AllowPublicEndpoint) &&
        specification.NetworkPolicy.Ports.All(port => AllowedPublishedPorts?.Contains(port) == true);
}

/// <summary>Construct only from current authenticated scope and persisted scoped-action grants.</summary>
public sealed record ComputeAdmissionDecision(bool Allowed, string? FailureCode, IReadOnlyList<ComputeActionAuthorization> Authority);

public static class ComputePolicy
{
    public static IReadOnlyList<string> RequiredProvisionActions(ComputeSpecification specification)
    {
        var actions = new List<string> { InfrastructureActions.Provision };
        if (specification.Persistence == ComputePersistence.Persistent) actions.Add(InfrastructureActions.Persist);
        if (specification.NetworkPolicy.Mode == ComputeNetworkMode.Private) actions.Add(InfrastructureActions.PrivateNetwork);
        if (specification.NetworkPolicy.AllowOutbound) actions.Add(InfrastructureActions.Outbound);
        if (specification.NetworkPolicy.Mode == ComputeNetworkMode.Inbound) actions.Add(InfrastructureActions.Inbound);
        if (specification.NetworkPolicy.PublicEndpoint) actions.Add(InfrastructureActions.PublicEndpoint);
        if (specification.NetworkPolicy.Ports.Count > 0) actions.Add(InfrastructureActions.PublishPort);
        return actions;
    }

    public static ComputeAdmissionDecision Evaluate(ComputeSpecification specification, ComputeTemplate template,
        ComputeGrantConstraints constraints, IReadOnlyList<ComputeActionAuthorization> currentAuthority,
        int activeEnvironments, DateTimeOffset now)
    {
        if (!specification.IsValid) return Denied("InvalidSpecification");
        if (!template.Matches(specification)) return Denied("TemplateUnavailable");
        if (constraints.EnvironmentId is not null || !constraints.Allows(specification, activeEnvironments)) return Denied("ConstraintExceeded");
        DateTimeOffset expiry;
        try { expiry = now.AddSeconds(specification.LifetimeSeconds); }
        catch (ArgumentOutOfRangeException) { return Denied("InvalidLifetime"); }
        var required = RequiredProvisionActions(specification);
        var selected = new List<ComputeActionAuthorization>();
        foreach (var action in required)
        {
            var grant = currentAuthority.FirstOrDefault(x => x.Action == action &&
                x.GrantId != Guid.Empty && x.Revision > 0 && x.ExpiresAt >= expiry);
            if (grant is null) return Denied("ActionGrantRequired");
            selected.Add(grant);
        }
        return new(true, null, selected);
    }

    private static ComputeAdmissionDecision Denied(string code) => new(false, code, []);
}
