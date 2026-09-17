using System.Text.Json;
using CSweet.Contracts.Analytics;
using CSweet.Contracts.Core;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public sealed partial class BenchmarkService
{
    public async Task<bool> AdvanceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        Guid? evaluateId = null;
        await using (var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            // One platform dispatcher owns admission; replicas use a transaction-scoped lock.
            if (db.Database.IsNpgsql())
            {
                var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(728348205) AS \"Value\"").SingleAsync(ct);
                if (!acquired) return false;
            }
            var wakes = await db.BenchmarkWakes.Where(x => x.ProcessedAt == null).OrderBy(x => x.CreatedAt).Take(64).ToListAsync(ct);
            var trialIds = wakes.Where(x => x.TrialId.HasValue).Select(x => x.TrialId!.Value).ToArray();
            var organizationsToWake = wakes.Where(x => x.OrganizationId.HasValue).Select(x => x.OrganizationId!.Value).ToArray();
            var candidates = await db.BenchmarkTrials.Where(x =>
                    (x.Status == "Pending" || x.Status == "Running" || x.Status == "Blocked" ||
                     x.Status == "DeclaredComplete" && (x.EvaluationStatus == "Pending" || x.EvaluationStatus == "Evaluating" || x.EvaluationStatus == "AwaitingHuman")) &&
                    (x.Status != "Pending" || !db.BenchmarkCampaigns.Any(c => c.Id == x.CampaignId && c.Scheduling == "Sequential") ||
                        !db.BenchmarkTrials.Any(p => p.CampaignId == x.CampaignId && p.ExecutionOrder < x.ExecutionOrder &&
                            p.Status != "DeclaredComplete" && p.Status != "Failed" && p.Status != "Cancelled")) &&
                    (x.NextRecoveryAt <= now || trialIds.Contains(x.Id) || organizationsToWake.Contains(x.OrganizationId ?? Guid.Empty)))
                .OrderBy(x => x.NextRecoveryAt).ThenBy(x => x.ExecutionOrder).Take(32).ToListAsync(ct);
            foreach (var wake in wakes) wake.ProcessedAt = now;
            BenchmarkTrial? selected = null;
            foreach (var candidate in candidates)
            {
                if (candidate.EvaluationStatus == "Evaluating" && candidate.NextRecoveryAt > now) continue;
                selected = candidate; break;
            }
            if (selected is null)
            {
                await db.SaveChangesAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);
                return false;
            }
            var trial = selected;
            var campaign = await db.BenchmarkCampaigns.SingleAsync(x => x.Id == trial.CampaignId, ct);
            var definition = await db.BenchmarkDefinitions.SingleAsync(x => x.Id == campaign.DefinitionId, ct);
            var b = Blueprint(definition);
            trial.NextRecoveryAt = now.AddMinutes(1); trial.Revision++;
            if (trial.Status == "Pending")
            {
                if (tx is not null) await tx.CreateSavepointAsync("provision", ct);
                try { await ProvisionAsync(trial, campaign, definition, b, ct); }
                catch (Exception error) when (error is not OperationCanceledException && tx is not null)
                {
                    await tx.RollbackToSavepointAsync("provision", ct);
                    var id = trial.Id; var campaignId = campaign.Id;
                    db.ChangeTracker.Clear();
                    trial = await db.BenchmarkTrials.SingleAsync(x => x.Id == id, ct);
                    campaign = await db.BenchmarkCampaigns.SingleAsync(x => x.Id == campaignId, ct);
                    trial.Status = "Failed"; trial.FinishedAt = now; trial.Revision++;
                    trial.Detail = "Provisioning failed and was rolled back. Check platform diagnostics and launch a new campaign after correcting the configuration.";
                }
            }
            else if (trial.Status is "Running" or "Blocked")
            {
                var project = await db.Workstreams.SingleOrDefaultAsync(x => x.Id == trial.WorkstreamId && x.OrganizationId == trial.OrganizationId, ct);
                var completedAt = await db.WorkLifecycleEvents.Where(x => x.OrganizationId == trial.OrganizationId &&
                        x.ResourceId == trial.WorkstreamId && x.ResourceKind == "Project" && x.Status == "Completed" &&
                        x.OccurredAt >= trial.StartedAt)
                    .OrderBy(x => x.OccurredAt).Select(x => (DateTimeOffset?)x.OccurredAt).FirstOrDefaultAsync(ct);
                if (completedAt.HasValue)
                {
                    trial.DeclaredCompletedAt = completedAt;
                    trial.Status = "DeclaredComplete";
                    trial.SubmissionJson = await SnapshotAsync(trial, completedAt.Value, ct);
                    trial.FinishedAt = now;
                    trial.Detail = "Delivery declared complete. The submitted revisions are frozen for evaluation.";
                    await StopWorkAsync(trial, ct);
                }
                else if (project?.Status == WorkstreamStatus.Cancelled)
                {
                    trial.Status = "Cancelled"; trial.FinishedAt = now;
                    trial.Detail = "The business cancelled its project.";
                    await StopWorkAsync(trial, ct);
                }
                else
                {
                    trial.Status = project?.Status == WorkstreamStatus.Blocked ? "Blocked" : "Running";
                    trial.Detail = trial.Status == "Blocked" ? "Waiting for the business to resolve blocked work. No benchmark timeout applies." : "Business is executing its goal.";
                }
            }
            if (trial.Status == "DeclaredComplete" && trial.EvaluationStatus == "Pending")
            {
                trial.EvaluationStatus = "Evaluating";
                trial.NextRecoveryAt = now.AddMinutes(20);
                evaluateId = trial.Id;
            }
            else if (trial.EvaluationStatus == "Evaluating" && trial.Status == "DeclaredComplete")
            {
                // A wake is not a grant to repeat a provider call after an uncertain crash.
                trial.EvaluationStatus = "Incomplete";
                trial.Detail = "Evaluation was interrupted. Recorded results are retained; no model request was automatically repeated.";
            }
            else if (trial.Status == "DeclaredComplete" && trial.EvaluationStatus == "AwaitingHuman")
            {
                var humanKeys = await db.BenchmarkAssessments.Where(x => x.TrialId == trial.Id && x.Kind == "Human")
                    .Select(x => x.CriterionKey).Distinct().ToListAsync(ct);
                if (b.Criteria.Where(x => x.Kind == "Rubric").All(x => humanKeys.Contains(x.Key))) trial.EvaluationStatus = "Complete";
            }
            var anyActive = await db.BenchmarkTrials.AnyAsync(x => x.CampaignId == campaign.Id && x.Id != trial.Id &&
                x.Status != "DeclaredComplete" && x.Status != "Failed" && x.Status != "Cancelled", ct);
            if (!anyActive && trial.Status is "DeclaredComplete" or "Failed" or "Cancelled")
            { campaign.Status = "DeliveryFinished"; campaign.Revision++; }
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        if (evaluateId.HasValue) await EvaluateAsync(evaluateId.Value, ct);
        return true;
    }

    private async Task ProvisionAsync(BenchmarkTrial trial, BenchmarkCampaign campaign,
        BenchmarkDefinition definition, BenchmarkBlueprint b, CancellationToken ct)
    {
        // Admission and all database provisioning share the caller's transaction. No agent can see a half-built business.
        using var manifest = JsonDocument.Parse(definition.ManifestJson);
        foreach (var hire in b.HiringPlan)
        {
            var agent = await db.AgentDefinitions.AsNoTracking().Include(x => x.Configuration).SingleOrDefaultAsync(x =>
                x.Id == hire.AgentDefinitionId && x.PackageVersionId == hire.PackageVersionId && x.IsAvailableForHire, ct);
            var pinned = manifest.RootElement.GetProperty("agents").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == hire.AgentDefinitionId);
            if (agent is null || pinned.GetProperty("configurationDigest").GetString() != ConfigurationDigest(agent))
            {
                trial.Status = "Failed"; trial.FinishedAt = clock.GetUtcNow();
                trial.Detail = "A pinned agent version or its initial configuration changed. Create a new definition version.";
                return;
            }
        }
        var variant = b.Variants[trial.VariantIndex];
        var org = await organizations.CreateAsync(new CreateOrganizationRequest(
            $"{b.Name} · {variant.Name} · {trial.Repetition}", null, b.Goal, "Benchmark", b.Goal, null), ct, campaign.CreatedBy);
        if (!org.Succeeded || org.Organization is null) throw new InvalidOperationException(org.Message);
        trial.OrganizationId = org.Organization.Id;
        await db.SaveChangesAsync(ct);
        var owner = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == trial.OrganizationId &&
            x.ApplicationUserId == campaign.CreatedBy && x.IsActive, ct);
        var hired = new Dictionary<string, OrganizationUser>(StringComparer.Ordinal);
        foreach (var hire in b.HiringPlan)
        {
            var role = new Role { Id = Guid.NewGuid(), OrganizationId = trial.OrganizationId.Value, Name = hire.RoleKey,
                Description = $"Benchmark role: {hire.DisplayName}", AuthorityLevel = AuthorityLevel.Autonomous,
                CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            db.CoreRoles.Add(role); await db.SaveChangesAsync(ct);
            var manager = hire.ReportsToRoleKey is null ? owner.Id : hired[hire.ReportsToRoleKey].Id;
            var result = await employees.CreateAsync(trial.OrganizationId.Value,
                new CreateOrganizationUserRequest(hire.DisplayName, null, (int)(hired.Count == 0 ? OrganizationPermissionLevel.Manager : OrganizationPermissionLevel.Contributor),
                    (int)EmployeeType.Agent, RoleId: role.Id, ReportsToOrganizationUserId: manager,
                    AgentDefinitionId: hire.AgentDefinitionId), ct, campaign.CreatedBy, "Benchmark");
            if (!result.Succeeded || result.OrganizationUser is null) throw new InvalidOperationException(result.Message);
            hired[hire.RoleKey] = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == result.OrganizationUser.Id, ct);
        }
        var lead = hired[b.HiringPlan[0].RoleKey];
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = trial.OrganizationId.Value,
            Name = b.Name, Outcome = b.Goal, Status = WorkstreamStatus.Active, LifecycleStage = "Active",
            AccountableManagerOrganizationUserId = lead.Id,
            SuccessCriteriaJson = JsonSerializer.Serialize(b.Criteria.Select(x => x.Title), Json),
            CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        db.Workstreams.Add(project); trial.WorkstreamId = project.Id;
        foreach (var seed in b.Inputs)
        {
            var artifactId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
            db.CoreArtifacts.Add(new Artifact { Id = artifactId, OrganizationId = trial.OrganizationId.Value,
                WorkstreamId = project.Id, Title = seed.Title, Content = seed.Content, Type = ArtifactType.Document,
                LatestRevisionId = revisionId, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
                CreatedByOrganizationUserId = owner.Id, CreatorDisplayName = owner.DisplayName, DocumentType = "benchmark-input" });
            db.ArtifactRevisions.Add(new ArtifactRevision { Id = revisionId, ArtifactId = artifactId,
                OrganizationId = trial.OrganizationId.Value, Number = 1, Content = seed.Content,
                ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed.Content))),
                CreatedAt = clock.GetUtcNow(), CreatedByOrganizationUserId = owner.Id, CreatorDisplayName = owner.DisplayName,
                IdempotencyKey = $"benchmark-input:{trial.Id}:{artifactId}" });
        }
        var organization = await db.CoreOrganizations.SingleAsync(x => x.Id == trial.OrganizationId, ct);
        organization.Status = OrganizationStatus.Active;
        await db.SaveChangesAsync(ct);
        var chat = await onboarding.EnsureAsync(trial.OrganizationId.Value, lead, campaign.CreatedBy, cancellationToken: ct);
        if (!chat.Succeeded || !chat.ConversationId.HasValue) throw new InvalidOperationException(chat.Message);
        trial.Status = "Running"; trial.StartedAt = clock.GetUtcNow();
        trial.Detail = "Initial workforce provisioned. Goal released.";
        await db.SaveChangesAsync(ct);
        var message = $"Deliver this goal: {b.Goal}\nUse the existing project/workstream {project.Id:D} ({project.Name}). " +
            "Associate delivery boards and artifacts with that workstream. Declare it Completed when the product is ready. " +
            "Staffing may adapt within approved authority. " +
            $"Assistance mode: {b.AssistanceMode}{(b.ApprovalsOnly ? "; human approvals only" : "")}. " +
            "Acceptance criteria: " + string.Join("; ", b.Criteria.Select(x => x.Title));
        var turn = await turns.StartForAgentAsync(trial.OrganizationId.Value, chat.ConversationId.Value, lead.Id,
            message, owner.Id, sourceProvider: "Benchmark", idempotencyKey: $"benchmark-goal:{trial.Id}", cancellationToken: ct);
        if (turn is null) throw new InvalidOperationException("The benchmark goal could not be delivered to the initial lead.");
    }

    private async Task StopWorkAsync(BenchmarkTrial trial, CancellationToken ct)
    {
        if (trial.OrganizationId is not { } organization) return;
        var key = organization.ToString();
        foreach (var work in await db.AgentWorkItems.Where(x => x.OrganizationId == key &&
                     (x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased)).ToListAsync(ct))
        {
            work.Status = AgentWorkStatus.Cancelled; work.CompletedAt = clock.GetUtcNow();
            work.LastError = "Benchmark delivery has ended.";
        }
        foreach (var schedule in await db.AgentSchedules.Where(x => x.AgentInstallation!.BusinessId == key).ToListAsync(ct))
        { schedule.IsEnabled = false; schedule.NextTickAt = null; }
        foreach (var installation in await db.AgentInstallations.Where(x => x.BusinessId == key).ToListAsync(ct))
            installation.IsEnabled = false;
    }

    private sealed record SubmissionArtifact(Guid ArtifactId, Guid RevisionId, string Title, string Digest, string Content);
    private sealed record SubmissionValidation(Guid Id, Guid PublicationId, Guid RepositoryId, string CommitSha,
        string Status, string ResultsJson, Guid ValidatorInstallationId);
    private sealed record FrozenSubmission(DateTimeOffset DeclaredAt, IReadOnlyList<SubmissionArtifact> Artifacts,
        int UnresolvedWork, bool Truncated)
    {
        public IReadOnlyList<SubmissionValidation> Validations { get; init; } = [];
    }

    private async Task<string> SnapshotAsync(BenchmarkTrial trial, DateTimeOffset completedAt, CancellationToken ct)
    {
        var revisions = await db.ArtifactRevisions.AsNoTracking().Where(x => x.OrganizationId == trial.OrganizationId &&
                x.CreatedAt <= completedAt && x.Artifact!.WorkstreamId == trial.WorkstreamId && x.Artifact.DocumentType != "benchmark-input")
            .OrderByDescending(x => x.CreatedAt).Take(5001)
            .Select(x => new { x.Id, x.ArtifactId, x.Artifact!.Title, x.ContentSha256, x.Content, x.Number }).ToListAsync(ct);
        var latest = revisions.GroupBy(x => x.ArtifactId).Select(x => x.OrderByDescending(a => a.Number).First()).ToList();
        var used = 0; var artifacts = new List<SubmissionArtifact>(); var truncated = revisions.Count > 5000;
        foreach (var r in latest)
        {
            if (used + r.Content.Length > 2_000_000) { truncated = true; continue; }
            used += r.Content.Length;
            artifacts.Add(new(r.ArtifactId, r.Id, r.Title, r.ContentSha256, r.Content));
        }
        var unresolved = await db.CoreWorkTasks.CountAsync(x => x.OrganizationId == trial.OrganizationId &&
            x.Board!.WorkstreamId == trial.WorkstreamId && x.Status != WorkTaskStatus.Completed && x.Status != WorkTaskStatus.Cancelled, ct);
        var validations = await (from v in db.SourceControlValidations.AsNoTracking()
            join p in db.SourceControlPublications on v.PublicationId equals p.Id
            join w in db.SourceControlWorkspaces on p.WorkspaceId equals w.Id
            join item in db.CoreWorkTasks on w.WorkItemId equals item.Id
            where v.OrganizationId == trial.OrganizationId && p.OrganizationId == trial.OrganizationId &&
                item.Board!.WorkstreamId == trial.WorkstreamId && v.CompletedAt <= completedAt &&
                (v.SupersededAt == null || v.SupersededAt > completedAt) && v.CommitSha == p.CommitSha
            select new SubmissionValidation(v.Id, p.Id, p.RepositoryId, v.CommitSha, v.Status.ToString(), v.ResultsJson, v.ValidatorAgentInstallationId))
            .Take(501).ToListAsync(ct);
        return JsonSerializer.Serialize(new FrozenSubmission(completedAt, artifacts, unresolved, truncated || validations.Count > 500)
        { Validations = validations.Take(500).ToList() }, Json);
    }
}
