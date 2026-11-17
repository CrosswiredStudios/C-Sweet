using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using WorkforceCandidate = CSweet.Domain.Core.WorkforceCandidate;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private static WorkforceCandidate CreateCatalogCandidate(Guid organizationId, AvailableAgent agent, Guid? planId = null) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId,
        Source = agent.Source switch
        {
            AgentCatalogSource.Installed => "InstalledPlugin",
            AgentCatalogSource.LocalDirectory => "LocalDirectoryCatalog",
            AgentCatalogSource.FirstPartyCatalog => "CSweetEmbeddedCatalog",
            AgentCatalogSource.Marketplace => "CSweetMarketplace",
            _ => throw new InvalidOperationException("The selected catalog source is unsupported.")
        },
        ExternalCandidateId = agent.InstallationId?.ToString("D") ?? agent.AgentReference,
        DisplayName = agent.Name, CapabilitiesJson = JsonSerializer.Serialize(agent.Capabilities, JsonOptions),
        Score = agent.Score, EstimatedCost = agent.Price, Currency = agent.Currency,
        IsHuman = false, IsAvailable = true, WorkforcePlanId = planId,
        ExplanationJson = JsonSerializer.Serialize(new CandidateMetadata
        {
            ResourceType = "Agent", Rationale = $"{agent.Trust}. Catalog source: {agent.Source}.",
            RepositoryUrl = agent.RepositoryUrl, CatalogReference = agent.AgentReference
        }, JsonOptions)
    };

    // Staffing requirements describe what an employee can do, including using platform
    // services. Catalog Capabilities lists only provided services; it is not a complete
    // inventory. Use the same pinned package/grant evidence as the hiring review when
    // discovery metadata alone cannot establish a match. This does not grant any access.
    private async Task<bool> AssessHiringCapabilitiesAsync(WorkforcePlan plan, AvailableAgent agent,
        IReadOnlyList<string> required, CancellationToken token)
    {
        if (!required.Except(agent.Capabilities, StringComparer.Ordinal).Any()) return true;
        var candidate = CreateCatalogCandidate(plan.OrganizationId, agent);
        var snapshot = await BuildWorkflowSnapshotAsync(candidate, plan.Title, null, [], token, objective: plan.Objective);
        return SatisfiesHiringCapabilities(required, candidate, snapshot);
    }

    private static bool SatisfiesHiringCapabilities(IReadOnlyList<string> required,
        WorkforceCandidate candidate, WorkflowSnapshot snapshot) =>
        !required.Except(
            (snapshot.EmbeddedAgent?.ProvidedCapabilities ?? ReadStrings(candidate.CapabilitiesJson))
                .Concat(snapshot.ApprovedGrants), StringComparer.Ordinal).Any();
}
