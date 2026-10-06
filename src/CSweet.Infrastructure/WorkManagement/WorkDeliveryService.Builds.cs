using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private async Task ValidateBuildIdentitiesAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution,
        IReadOnlyList<Guid> ids, bool complete, CancellationToken ct)
    {
        var candidate = Candidate(execution)!;
        var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => x.OrganizationId == plan.OrganizationId &&
            x.WorkstreamId == plan.WorkstreamId && ids.Contains(x.Id)).ToListAsync(ct);
        if (ids.Count != candidate.Repositories.Count || builds.Count != ids.Count || ids.Distinct().Count() != ids.Count)
            throw new InvalidOperationException("Build readiness needs one certified build for every candidate repository.");
        foreach (var repository in candidate.Repositories)
        {
            var binding = Branches(plan).Single(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId && x.RepositoryId == repository.RepositoryId);
            var specification = binding.Build ?? throw new InvalidOperationException("Configure an exact certified build recipe, provider and target before release readiness.");
            var build = builds.SingleOrDefault(x => x.RepositoryId == repository.RepositoryId && x.SourceRevision == repository.CandidateCommitSha);
            if (build is null || build.ToolchainDefinitionId != specification.ToolchainDefinitionId ||
                build.ProviderInstallationId != specification.ProviderInstallationId || build.RecipeKey != specification.RecipeKey || build.TargetKey != specification.TargetKey ||
                !System.Text.Json.JsonElement.DeepEquals(Decode<System.Text.Json.JsonElement>(build.ConfigurationJson), specification.Configuration))
                throw new InvalidOperationException("The build does not match the agreed candidate, certified provider, recipe and configuration.");
            if (complete && (build.Status != DeliveryBuildStatuses.Succeeded || Decode<BuildOutputManifestEntry[]>(build.OutputsJson).Length == 0 ||
                Decode<BuildExecutionProvenance>(build.ProvenanceJson)?.SourceRevision != repository.CandidateCommitSha))
                throw new InvalidOperationException("Release readiness requires successful candidate-bound packaging and provenance for every repository.");
        }
    }
}
