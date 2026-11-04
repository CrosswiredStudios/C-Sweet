using System.Text.Json;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.PersonalAssistant;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.PersonalAssistant;

public sealed partial class RepositoryOnboardingService
{
    private async Task RequireBusinessOwnerAsync(Guid user, Guid org, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == org && x.ApplicationUserId == user &&
            x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner &&
            db.CoreOrganizations.Any(o => o.Id == org && o.Status == OrganizationStatus.Active), ct))
            throw new UnauthorizedAccessException("Current business ownership is required to import projects.");
    }

    public async Task<RepositoryOnboardingDraftResponse> StartBusinessImportAsync(Guid userId, Guid organizationId, Guid? draftId, CancellationToken token = default)
    {
        await RequireBusinessOwnerAsync(userId, organizationId, token);
        RepositoryOnboardingDraft draft;
        if (draftId.HasValue)
        {
            draft = await RequireAsync(userId, draftId.Value, token);
            if (draft.OrganizationId.HasValue && draft.OrganizationId != organizationId) throw new UnauthorizedAccessException("The draft belongs to another business.");
            if (draft.OrganizationId is null && draft.Status is "Preparing" or "Analyzing" or "Creating")
                throw new InvalidOperationException("Wait for the saved operation to stop before moving this draft to a business.");
        }
        else
        {
            draft = await db.RepositoryOnboardingDrafts.Include(x => x.Sources).Where(x => x.OrganizationId == organizationId &&
                x.Status != "Discarded" && x.FinishedAt == null && x.Status != "Created").OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(token)
                ?? new() { Id = Guid.NewGuid(), ApplicationUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            if (db.Entry(draft).State == EntityState.Detached) db.RepositoryOnboardingDrafts.Add(draft);
        }
        var verified = JsonSerializer.Deserialize<HashSet<long>>(draft.VerifiedInstallationsJson) ?? [];
        foreach (var installation in await db.SourceControlConnections.Where(x => x.OrganizationId == organizationId && x.SourceAccessInstallationId != null && x.Status == CSweet.Domain.Setup.SourceControlConnectionStatus.Connected).Select(x => x.SourceAccessInstallationId!.Value).ToListAsync(token)) verified.Add(installation);
        foreach (var saved in await db.RepositoryOnboardingDrafts.Where(x => x.OrganizationId == organizationId && x.Status != "Discarded").Select(x => x.VerifiedInstallationsJson).ToListAsync(token))
            verified.UnionWith(JsonSerializer.Deserialize<long[]>(saved) ?? []);
        draft.VerifiedInstallationsJson = JsonSerializer.Serialize(verified);
        draft.OrganizationId = organizationId;
        if (draft.Status is "Created" or "CreationFailed") draft.Status = "Sources";
        draft.ActionUri = $"/organizations/{organizationId:D}/onboarding?draft={draft.Id:D}";
        await SaveAsync(draft, token);
        return Response(draft);
    }

    public async Task<IReadOnlyList<RepositoryOnboardingDraftResponse>> BusinessImportsAsync(Guid userId, Guid organizationId, CancellationToken token = default)
    {
        await RequireBusinessOwnerAsync(userId, organizationId, token);
        return (await db.RepositoryOnboardingDrafts.AsNoTracking().Include(x => x.Sources).Where(x => x.OrganizationId == organizationId && x.Status != "Discarded")
            .OrderByDescending(x => x.UpdatedAt).Take(50).ToListAsync(token)).Select(Response).ToArray();
    }

    public async Task<RepositoryOnboardingDraftResponse> FinishAsync(Guid userId, Guid id, long expectedRevision, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        if (draft.FinishedAt.HasValue) return Response(draft);
        if (draft.Revision != expectedRevision) throw new DbUpdateConcurrencyException("The import changed. Refresh before finishing.");
        draft.FinishedAt = DateTimeOffset.UtcNow;
        await SaveAsync(draft, token);
        return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> AddProjectsAsync(Guid userId, Guid id, AddImportedProjectsRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        var org = draft.OrganizationId ?? throw new InvalidOperationException("Choose or create a business before adding projects.");
        if (request.ProjectNames is null || request.ProjectNames.Count is < 1 or > 50 || request.ProjectNames.Any(x => x.Key == Guid.Empty || string.IsNullOrWhiteSpace(x.Value) || x.Value.Trim().Length > 160) ||
            request.ProjectNames.Keys.Any(x => !draft.Sources.Any(s => s.Id == x))) throw new ArgumentException("Review the selected repositories and project names (up to 160 characters).");
        var selected = draft.Sources.Where(x => request.ProjectNames.Keys.Contains(x.Id)).ToArray();
        var repositories = selected.Select(x => x.RepositoryId).ToArray();
        // A lost successful response can be replayed even after profiling advances the session revision.
        // Committed names/profile edits are retained; importing again is never a rename operation.
        if (request.ExpectedRevision <= draft.Revision && selected.All(x => x.Attached) &&
            await db.ProjectRepositoryAssociations.CountAsync(x => x.OrganizationId == org && repositories.Contains(x.RepositoryId), token) == selected.Length)
            return Response(draft);
        RequireEditable(draft, request.ExpectedRevision);
        foreach (var pair in request.ProjectNames)
        {
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            try
            {
                if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"business-import:" + org}, 0))", token);
                draft = await RequireAsync(userId, id, token);
                var source = draft.Sources.Single(x => x.Id == pair.Key);
                if (source.Kind == "GitHub") await workflow.AttachGitHubAsync(draft, source, token);
                else await workflow.ImportZipAsync(draft, source, token);
                var association = await db.ProjectRepositoryAssociations.SingleOrDefaultAsync(x => x.OrganizationId == org && x.RepositoryId == source.RepositoryId, token);
                if (association is null)
                {
                    var now = DateTimeOffset.UtcNow;
                    var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Name = pair.Value.Trim(),
                        Outcome = "Review the imported project and agree its next goals.", Status = WorkstreamStatus.Proposed,
                        LifecycleStage = "Onboarding", CreatedAt = now, UpdatedAt = now };
                    db.Workstreams.Add(project);
                    db.ProjectRepositoryAssociations.Add(new() { ProjectId = project.Id, OrganizationId = org, RepositoryId = source.RepositoryId, ImportSourceId = source.Id, CreatedAt = now });
                    db.ImportedProjectProfiles.Add(new() { ProjectId = project.Id, OrganizationId = org, ImportSourceId = source.Id, UpdatedAt = now });
                    BusinessSuggestionService.Queue(db, org, AppRealtimeEvents.ProjectImportChanged, project.Id);
                }
                source.Attached = true; source.Error = null;
                await db.SaveChangesAsync(token);
                if (tx is not null) await tx.CommitAsync(token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (tx is not null) await tx.RollbackAsync(token);
                db.ChangeTracker.Clear(); draft = await RequireAsync(userId, id, token);
                draft.Sources.Single(x => x.Id == pair.Key).Error = "This project could not be added. Check repository access and retry; other added projects are retained.";
                await SaveAsync(draft, token);
            }
        }
        draft = await RequireAsync(userId, id, token);
        draft.Status = "Sources"; draft.Error = null;
        await new BusinessSuggestionService(db).EnsureChiefAsync(org, token);
        await SaveAsync(draft, token);
        return Response(draft);
    }

    public async Task<IReadOnlyList<ImportedProjectResponse>> ProjectsAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        var ids = draft.Sources.Select(x => x.RepositoryId).ToArray();
        var rows = await (from a in db.ProjectRepositoryAssociations.AsNoTracking()
                          join p in db.Workstreams.AsNoTracking() on a.ProjectId equals p.Id
                          join f in db.ImportedProjectProfiles.AsNoTracking() on p.Id equals f.ProjectId
                          where a.OrganizationId == draft.OrganizationId && ids.Contains(a.RepositoryId)
                          select new { a, p, f }).ToListAsync(token);
        return rows.Select(x => ProjectResponse(x.a, x.p, x.f)).ToArray();
    }

    public async Task<ImportedProjectResponse> AcceptProfileAsync(Guid userId, Guid id, Guid projectId, AcceptImportedProjectProfileRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        var association = await db.ProjectRepositoryAssociations.SingleAsync(x => x.OrganizationId == draft.OrganizationId && x.ProjectId == projectId, token);
        if (!draft.Sources.Any(x => x.RepositoryId == association.RepositoryId)) throw new UnauthorizedAccessException("This project is not in this import.");
        var profile = await db.ImportedProjectProfiles.SingleAsync(x => x.ProjectId == projectId, token);
        if (profile.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The project profile changed. Reload before saving.");
        var brief = request.Profile ?? throw new ArgumentException("A project profile is required.");
        if (brief.Summary is null || brief.Technology is null || brief.CurrentState is null || brief.Evidence is null || brief.Evidence.Count > 80 || brief.Uncertainties is null || brief.Uncertainties.Count > 10 || brief.Uncertainties.Any(x => x is null || x.Length > 2000)) throw new ArgumentException("Review the project profile fields.");
        if (brief.SourceId != association.ImportSourceId.ToString("D") || string.IsNullOrWhiteSpace(brief.Name) || brief.Name.Length > 160 ||
            brief.Summary.Length > 6000 || brief.Technology.Length > 512 || brief.CurrentState.Length > 2048 ||
            brief.Category is not ("Unknown" or "Software" or "VideoGame" or "Other")) throw new ArgumentException("Review the project profile fields.");
        if (brief.Evidence.Count > 0)
        {
            var evidenceSource = await db.RepositoryOnboardingSources.SingleAsync(x => x.Id == association.ImportSourceId, token);
            var evidenceDraft = await db.RepositoryOnboardingDrafts.SingleAsync(x => x.Id == evidenceSource.DraftId && x.OrganizationId == draft.OrganizationId, token);
            var input = evidenceSource.AnalysisInputJson is null ? null : JsonSerializer.Deserialize<RepositoryAnalysisInput>(evidenceSource.AnalysisInputJson, Json);
            RepositoryBriefValidator.ValidateEvidence([brief], input is null ? [] : [input]);
        }
        var actor = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == draft.OrganizationId && x.ApplicationUserId == userId && x.IsActive && x.ArchivedAt == null, token);
        var project = await db.Workstreams.SingleAsync(x => x.Id == projectId, token);
        project.Name = brief.Name.Trim(); project.Revision++; project.UpdatedAt = DateTimeOffset.UtcNow;
        profile.AcceptedJson = JsonSerializer.Serialize(brief, Json); profile.Status = "Accepted";
        profile.AcceptedByOrganizationUserId = actor.Id; profile.Error = null; profile.Revision++; profile.UpdatedAt = project.UpdatedAt;
        BusinessSuggestionService.Queue(db, project.OrganizationId, AppRealtimeEvents.ProjectImportChanged, project.Id);
        await db.SaveChangesAsync(token);
        return ProjectResponse(association, project, profile);
    }

    private static ImportedProjectResponse ProjectResponse(ProjectRepositoryAssociation a, Workstream p, ImportedProjectProfile f) =>
        new(p.Id, a.ImportSourceId, a.RepositoryId, p.Name, f.Status, f.Revision,
            f.InferredJson == "{}" ? null : JsonSerializer.Deserialize<RepositoryProjectBrief>(f.InferredJson, Json),
            f.AcceptedJson == "{}" ? null : JsonSerializer.Deserialize<RepositoryProjectBrief>(f.AcceptedJson, Json), f.Error);
}
