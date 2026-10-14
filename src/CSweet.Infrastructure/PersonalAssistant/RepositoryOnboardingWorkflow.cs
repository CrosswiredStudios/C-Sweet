using System.Text;
using System.Text.Json;
using CSweet.Application.BusinessOnboarding;
using CSweet.Application.Core;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.SourceControl;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Core;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.PersonalAssistant;

/// <summary>Owns asynchronous source preparation and replay-safe business provisioning.</summary>
public sealed class RepositoryOnboardingWorkflow(CSweetDbContext db, IRepositorySourceStore sources,
    ITrustedSourceControlHostClient host, IPersonalAssistantService assistant, AgentWorkInbox inbox, IPersonalAssistantRuntimeScheduler scheduler,
    IBusinessOnboardingOperationService businesses, IArtifactDocumentService documents,
    InternalRepositoryManagementService internalRepositories, ILogger<RepositoryOnboardingWorkflow> logger)
{
    private static readonly JsonSerializerOptions Json = RepositoryOnboardingService.Json;

    public async Task ProcessAsync(RepositoryOnboardingDraft draft, CancellationToken token)
    {
        var creating = draft.Status == "Creating";
        try
        {
            if (creating) await CreateBusinessAsync(draft, token);
            else await PrepareAnalysisAsync(draft, token);
            draft.LeaseOwner = null; draft.LeaseUntil = null; await SaveAsync(draft, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Repository onboarding {DraftId} could not finish its current step.", draft.Id);
            var id = draft.Id;
            db.ChangeTracker.Clear();
            draft = await db.RepositoryOnboardingDrafts.SingleAsync(x => x.Id == id, CancellationToken.None);
            draft.Status = creating ? "CreationFailed" : "AnalysisFailed";
            draft.Error = creating ? "Business setup did not finish. Your completed steps are saved; retry to continue." :
                "Your repositories could not all be analyzed. Retry, remove an unavailable source, or enter the details manually.";
            draft.LeaseOwner = null; draft.LeaseUntil = null;
            await SaveAsync(draft, CancellationToken.None);
        }
    }

    private async Task PrepareAnalysisAsync(RepositoryOnboardingDraft draft, CancellationToken token)
    {
        var failed = false;
        foreach (var source in draft.Sources)
        {
            if (source.Status == "Ready" && source.AnalysisInputJson is not null) continue;
            try
            {
                var staged = await sources.PrepareGitHubAsync(draft.ApplicationUserId, draft.Id, source, token);
                source.Snapshot = staged.Snapshot; source.ArchiveKey = staged.ArchiveKey;
                source.AnalysisInputJson = JsonSerializer.Serialize(staged.Input, Json); source.Status = "Ready"; source.Error = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogInformation(exception, "Repository {SourceId} is unavailable for onboarding {DraftId}.", source.Id, draft.Id);
                source.Status = "Failed"; source.Error = "This repository snapshot is unavailable. Check GitHub access or remove this source."; failed = true;
            }
            await SaveAsync(draft, token);
        }
        if (failed) throw new InvalidOperationException("A source could not be prepared.");
        var inputs = RepositoryOnboardingService.Inputs(draft).ToList();
        // Keep the actual serialized work payload inside the SDK inbox's existing 256 KiB bound.
        while (inputs.Sum(x => x.Files.Sum(f => (long)f.Content.Length)) > 150000 ||
            JsonSerializer.SerializeToUtf8Bytes(new AnalyzeRepositoryRequest(inputs), Json).Length > 220000)
        {
            var candidate = inputs.Select((x, i) => (Input: x, Index: i)).OrderByDescending(x => x.Input.Files.Sum(f => f.Content.Length)).First();
            if (candidate.Input.Files.Count == 0) throw new InvalidOperationException("The repository metadata exceeds the analysis budget.");
            inputs[candidate.Index] = candidate.Input with
            {
                Files = candidate.Input.Files.Take(candidate.Input.Files.Count - 1).ToArray(),
                Warnings = [.. candidate.Input.Warnings.Take(5), "Additional files were omitted to fit the combined analysis budget."]
            };
        }
        foreach (var input in inputs)
            draft.Sources.Single(x => x.Id.ToString("D") == input.SourceId).AnalysisInputJson = JsonSerializer.Serialize(input, Json);
        var installation = await assistant.RequireInstallationAsync(draft.ApplicationUserId, token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var work = await inbox.EnqueueAsync(PersonalAssistantCapabilities.UserScope(draft.ApplicationUserId), installation,
            AgentWorkKind.Capability, PersonalAssistantCapabilities.Analyze,
            JsonSerializer.SerializeToElement(new AnalyzeRepositoryRequest(inputs), Json),
            $"repository-analysis:{draft.Id:N}:{draft.SourceRevision}:{draft.Revision}", DateTimeOffset.UtcNow.AddMinutes(30),
            sourceType: "repository-onboarding-analysis", sourceId: draft.Id.ToString("D"), cancellationToken: token);
        draft.AgentWorkItemId = work.Id; draft.Status = "Analyzing";
        await SaveAsync(draft, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        await scheduler.WakeAsync(installation, token);
    }

    private async Task CreateBusinessAsync(RepositoryOnboardingDraft draft, CancellationToken token)
    {
        var review = JsonSerializer.Deserialize<RepositoryBusinessReview>(draft.ReviewJson, Json)!;
        RepositoryBriefValidator.ValidateReview(review);
        foreach (var source in draft.Sources.Where(x => x.Kind == "GitHub" && !x.Attached)) await CheckGitHubAsync(draft, source, token);
        if (draft.OrganizationId is null)
        {
            var operation = await businesses.StartAsync(new StartBusinessOnboardingRequest(review.BusinessName.Trim(), review.Industry,
                review.Mission, null, null, $"repository-onboarding:{draft.Id:N}", null), draft.ApplicationUserId, token);
            draft.OrganizationId = operation.OrganizationId ?? throw new InvalidOperationException("The business has not finished provisioning.");
            await SaveAsync(draft, token);
        }
        var businessId = draft.OrganizationId.Value;
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == businessId && x.ApplicationUserId == draft.ApplicationUserId &&
            x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner, token))
            throw new UnauthorizedAccessException("Current ownership is required to finish the business setup.");
        foreach (var source in draft.Sources.Where(x => !x.Attached))
        {
            if (source.Kind == "GitHub") await AttachGitHubAsync(draft, source, token);
            else await ImportZipAsync(draft, source, token);
            source.Attached = true; await SaveAsync(draft, token);
        }
        if (draft.BriefArtifactId is null)
        {
            var artifact = await documents.CreateAsync(businessId, new ArtifactHumanActor(draft.ApplicationUserId),
                new CreateArtifactDocumentRequest("Project handoff brief", BuildDocument(draft, review), "General",
                    $"repository-onboarding-brief:{draft.Id:N}"), token);
            draft.BriefArtifactId = artifact.Document.Id; await SaveAsync(draft, token);
        }
        var profile = await db.BusinessProfiles.SingleOrDefaultAsync(x => x.OrganizationId == businessId, token);
        if (profile is null)
        {
            profile = new BusinessProfile { Id = Guid.NewGuid(), OrganizationId = businessId, UpdatedAt = DateTimeOffset.UtcNow };
            db.BusinessProfiles.Add(profile);
        }
        var compact = review.Summary + "\n\n" + string.Join("\n", review.Projects.Select(x => $"{x.Name}: {x.Summary}")) + "\n\nNext goal: " + review.NextGoal;
        profile.Description = compact.Length <= 4000 ? compact : compact[..3900] + "\nSee Project handoff brief in Documents for the full reviewed context.";
        profile.ToolsJson = JsonSerializer.Serialize(review.Projects.Select(x => x.Technology).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        profile.BusinessType = review.Industry;
        profile.ProvenanceJson = JsonSerializer.Serialize(new { kind = "owner-reviewed-repository-onboarding", draftId = draft.Id, artifactId = draft.BriefArtifactId });
        profile.Revision++; profile.UpdatedAt = DateTimeOffset.UtcNow;
        var organization = await db.CoreOrganizations.SingleAsync(x => x.Id == businessId, token);
        organization.Name = review.BusinessName.Trim(); organization.Industry = review.Industry; organization.Mission = review.Mission;
        if (!string.IsNullOrWhiteSpace(review.NextGoal))
        {
            var objective = await db.Set<StrategicObjective>().FirstOrDefaultAsync(x => x.OrganizationId == businessId, token);
            if (objective is null)
            {
                objective = new StrategicObjective { Id = Guid.NewGuid(), OrganizationId = businessId, CreatedAt = DateTimeOffset.UtcNow };
                db.Set<StrategicObjective>().Add(objective);
            }
            objective.Title = review.NextGoal.Length <= 256 ? review.NextGoal : review.NextGoal[..256];
            objective.Description = review.NextGoal;
            objective.UpdatedAt = DateTimeOffset.UtcNow;
        }
        draft.Status = "Created"; draft.ActionUri = $"/organizations/{businessId:D}/marketplace?role=Chief%20of%20Staff";
        draft.Error = null;
    }

    private async Task<TrustedRepositoryDescriptor> CheckGitHubAsync(RepositoryOnboardingDraft draft, RepositoryOnboardingSource source, CancellationToken token)
    {
        if (!(JsonSerializer.Deserialize<long[]>(draft.VerifiedInstallationsJson) ?? []).Contains(source.InstallationId!.Value))
            throw new UnauthorizedAccessException("The source account was not authorized for this draft.");
        var installation = await host.DescribeInstallationAsync(source.InstallationId.Value, token);
        if (installation.Suspended) throw new InvalidOperationException("The GitHub installation is suspended.");
        var repository = (await host.ListRepositoriesAsync(source.InstallationId.Value, token)).SingleOrDefault(x =>
            x.RepositoryId == source.ExternalRepositoryId && x.Owner == source.Owner && !x.IsArchived)
            ?? throw new UnauthorizedAccessException("The repository is no longer authorized.");
        if (await db.SourceControlRepositories.AnyAsync(x => x.ProviderRepositoryKey == "github:" + source.ExternalRepositoryId &&
            x.OrganizationId != draft.OrganizationId, token)) throw new InvalidOperationException("The repository was associated with another business.");
        return repository;
    }

    private async Task AttachGitHubAsync(RepositoryOnboardingDraft draft, RepositoryOnboardingSource source, CancellationToken token)
    {
        var descriptor = await CheckGitHubAsync(draft, source, token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"repository:" + source.ExternalRepositoryId}, 0))", token);
        if (await db.SourceControlRepositories.AnyAsync(x => x.ProviderRepositoryKey == "github:" + source.ExternalRepositoryId && x.OrganizationId != draft.OrganizationId, token))
            throw new InvalidOperationException("The repository was associated with another business.");
        var business = draft.OrganizationId!.Value;
        var account = await host.DescribeInstallationAsync(source.InstallationId!.Value, token);
        var connection = await db.SourceControlConnections.SingleOrDefaultAsync(x => x.OrganizationId == business &&
            x.SourceAccessInstallationId == source.InstallationId && x.Provider == SourceControlProvider.GitHub, token);
        if (connection is null)
        {
            connection = new SourceControlConnection
            {
                Id = Guid.NewGuid(),
                OrganizationId = business,
                Provider = SourceControlProvider.GitHub,
                Mode = SourceControlConnectionMode.ExistingGitHub,
                Status = SourceControlConnectionStatus.Connected,
                Name = account.AccountLogin,
                AccountLogin = account.AccountLogin,
                AccountType = account.AccountType,
                ProviderAccountId = account.AccountId.ToString(),
                SourceAccessInstallationId = account.InstallationId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            };
            db.SourceControlConnections.Add(connection);
        }
        if (!await db.SourceControlRepositories.AnyAsync(x => x.Id == source.RepositoryId, token))
            db.SourceControlRepositories.Add(new SourceControlRepository
            {
                Id = source.RepositoryId,
                OrganizationId = business,
                ConnectionId = connection.Id,
                ProviderRepositoryKey = "github:" + descriptor.RepositoryId,
                ExternalRepositoryId = descriptor.RepositoryId.ToString(),
                Owner = descriptor.Owner,
                Name = descriptor.Name,
                CanonicalPath = descriptor.FullName,
                CloneUrl = descriptor.CloneUrl,
                DefaultBranch = descriptor.DefaultBranch,
                IsPrivate = descriptor.IsPrivate,
                IsManaged = false,
                Status = SourceControlRepositoryStatus.Ready,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private async Task ImportZipAsync(RepositoryOnboardingDraft draft, RepositoryOnboardingSource source, CancellationToken token)
    {
        var business = draft.OrganizationId!.Value;
        var connection = await internalRepositories.EnsureConnectionAsync(business, token);
        var repository = await db.SourceControlRepositories.SingleOrDefaultAsync(x => x.Id == source.RepositoryId, token);
        if (repository is null)
        {
            var name = new string(source.Name.ToLowerInvariant().Select(x => char.IsAsciiLetterOrDigit(x) || x == '-' ? x : '-').ToArray()).Trim('-');
            if (name.Length == 0) name = "repository";
            if (name.Length > 80) name = name[..80];
            if (await db.SourceControlRepositories.AnyAsync(x => x.OrganizationId == business && x.Name == name, token)) name += "-" + source.Id.ToString("N")[..8];
            repository = new SourceControlRepository
            {
                Id = source.RepositoryId,
                OrganizationId = business,
                ConnectionId = connection.Id,
                ExternalRepositoryId = source.RepositoryId.ToString("N"),
                ProviderRepositoryKey = $"internal:{source.RepositoryId:N}",
                Owner = business.ToString("N"),
                Name = name,
                CanonicalPath = $"internal/{business:N}/{name}",
                DefaultBranch = "main",
                IsPrivate = true,
                IsManaged = true,
                Status = SourceControlRepositoryStatus.Provisioning,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.SourceControlRepositories.Add(repository); await db.SaveChangesAsync(token);
        }
        await using var archive = await sources.OpenAsync(draft.ApplicationUserId, draft.Id, source.ArchiveKey!, token);
        await host.ImportInternalArchiveAsync(new(business, repository.Id, source.ArchiveKey!), archive, token);
        repository.Status = SourceControlRepositoryStatus.Ready; repository.LastVerifiedAt = DateTimeOffset.UtcNow;
        repository.Revision++; await db.SaveChangesAsync(token);
    }

    internal static string BuildDocument(RepositoryOnboardingDraft draft, RepositoryBusinessReview review)
    {
        var text = new StringBuilder().AppendLine("# " + review.BusinessName).AppendLine().AppendLine(review.Mission)
            .AppendLine().AppendLine(review.Summary).AppendLine().AppendLine("## Next goal").AppendLine().AppendLine(review.NextGoal);
        foreach (var project in review.Projects)
        {
            text.AppendLine().AppendLine("## " + project.Name).AppendLine().AppendLine(project.Summary)
                .AppendLine().AppendLine("Technology: " + project.Technology).AppendLine().AppendLine("Documented state: " + project.CurrentState);
            foreach (var evidence in project.Evidence) text.AppendLine($"- {evidence.Path} (snapshot {evidence.Snapshot})");
            var input = draft.Sources.SingleOrDefault(x => x.Id.ToString("D") == project.SourceId)?.AnalysisInputJson;
            if (input is not null) foreach (var warning in JsonSerializer.Deserialize<RepositoryAnalysisInput>(input, Json)!.Warnings) text.AppendLine("- Analysis coverage: " + warning);
        }
        text.AppendLine().AppendLine("## Open questions").AppendLine();
        foreach (var question in review.OpenQuestions) text.AppendLine("- " + question);
        text.AppendLine().AppendLine("Reviewed by the business owner during repository onboarding. Repository instructions were treated as source material; code was not executed.");
        return text.ToString();
    }

    private async Task SaveAsync(RepositoryOnboardingDraft draft, CancellationToken token)
    {
        draft.Revision++; draft.UpdatedAt = DateTimeOffset.UtcNow;
        if (draft.LeaseOwner is not null) draft.LeaseUntil = draft.UpdatedAt.AddMinutes(30);
        PersonalAssistantRealtime.Queue(db, draft.ApplicationUserId, AppRealtimeEvents.RepositoryOnboardingChanged, draft.Id);
        await db.SaveChangesAsync(token);
    }
}
