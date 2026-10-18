using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

internal static class TaskIntegrationRework
{
    // Existing sprint revisions are immutable. The v1 trusted action may reuse only
    // that pinned policy's explicit technical rejection path, never invent an assignee.
    internal static WorkOrchestrationTransition? Transition(WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage, string outcome)
    {
        var configured = policy.Transitions.SingleOrDefault(x => x.FromStageKey == stage.StageKey && x.OutcomeCode == outcome);
        if (stage.StageKey != "task-integration" || stage.PlatformAction != TaskIntegrationWorkActionExecutor.ActionName ||
            outcome is not ("integration_conflicts" or "target_changed")) return configured;
        if (configured is not null)
            return (outcome == "integration_conflicts" && configured.ToStageKey is "development" or "specialist-execution" ||
                outcome == "target_changed" && configured.ToStageKey == "technical-review") ? configured : null;
        var rejected = policy.Transitions.SingleOrDefault(x => x.FromStageKey == "technical-review" && x.OutcomeCode == "rejected" &&
            (x.ToStageKey == "development" || x.ToStageKey == "specialist-execution"));
        return rejected is null ? null : new WorkOrchestrationTransition { FromStageKey = stage.StageKey, OutcomeCode = outcome,
            ToStageKey = outcome == "target_changed" ? "technical-review" : rejected.ToStageKey,
            MaximumTraversals = rejected.MaximumTraversals ?? 3 };
    }

    internal static async Task SeedWorkspaceAsync(CSweetDbContext db, WorkStageExecution completed,
        WorkStageExecution author, string outputJson, DateTimeOffset now, CancellationToken ct)
    {
        if (completed.StageKey != "task-integration" || completed.PlatformAction != TaskIntegrationWorkActionExecutor.ActionName ||
            completed.LastOutcomeCode != "integration_conflicts" || author.AgentInstallationId is not { } installation) return;
        using var output = JsonDocument.Parse(outputJson);
        var data = output.RootElement;
        var item = completed.ItemExecution!.WorkItem!;
        var publicationId = data.GetProperty("publicationId").GetGuid();
        var publication = await db.SourceControlPublications.Include(x => x.Workspace).SingleAsync(x =>
            x.Id == publicationId && x.OrganizationId == item.OrganizationId, ct);
        var original = publication.Workspace!;
        var sha = data.GetProperty("reworkCommitSha").GetString()!;
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit) || original.WorkItemId != item.Id ||
            original.AssignmentRevision != item.AssignmentRevision - 1 || publication.Status == SourceControlPublicationStatus.Merged ||
            publication.CommitSha != data.GetProperty("sourceCommitSha").GetString() ||
            publication.RepositoryId != data.GetProperty("repositoryId").GetGuid() ||
            publication.TargetBranch != data.GetProperty("targetBranch").GetString())
            throw new InvalidOperationException("Integration rework no longer matches the exact prior publication and assignment.");
        var workspaceId = Guid.NewGuid();
        db.SourceControlWorkspaces.Add(new SourceControlWorkspace { Id = workspaceId, WorkspaceKey = $"rework-pending:{workspaceId:N}", OrganizationId = item.OrganizationId,
            TeamId = original.TeamId, RepositoryId = publication.RepositoryId, AgentInstallationId = installation,
            WorkItemId = item.Id, AssignmentRevision = item.AssignmentRevision,
            BranchName = $"csweet/task/{item.Id:N}/r{item.AssignmentRevision}", IntegrationTargetBranch = publication.TargetBranch,
            BaseCommitSha = sha, Status = SourceControlWorkspaceStatus.Preparing, CreatedAt = now, UpdatedAt = now, Revision = 1 });
    }
}
