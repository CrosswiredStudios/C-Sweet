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

/// <summary>Owns asynchronous, bounded profiling of projects that have already been imported.</summary>
public sealed class RepositoryOnboardingWorkflow(CSweetDbContext db, IRepositorySourceStore sources,
    ITrustedSourceControlHostClient host, IPersonalAssistantService assistant, AgentWorkInbox inbox, IPersonalAssistantRuntimeScheduler scheduler,
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
            if (!creating && draft.Status != "Preparing")
            {
                // A durable job or an owner review may already have advanced the session.
                // Wake/lease failures cannot invalidate committed work or owner decisions.
                draft.LeaseOwner = null; draft.LeaseUntil = null;
                await SaveAsync(draft, CancellationToken.None);
                return;
            }
            draft.Status = creating ? "CreationFailed" : "AnalysisFailed";
            draft.Error = creating ? "Business setup did not finish. Your completed steps are saved; retry to continue." :
                "Your repositories could not all be analyzed. Retry, remove an unavailable source, or enter the details manually.";
            foreach (var profile in await db.ImportedProjectProfiles.Where(x => x.AnalysisDraftId == draft.Id && x.Status == "Preparing").ToListAsync(CancellationToken.None))
            { profile.Status = "Failed"; profile.Error = "Profiling could not finish. Retry or enter details manually; your project is saved."; profile.UpdatedAt = DateTimeOffset.UtcNow; profile.Revision++; CSweet.Infrastructure.Core.BusinessSuggestionService.Queue(db, profile.OrganizationId, AppRealtimeEvents.ProjectImportChanged, profile.ProjectId); }
            draft.LeaseOwner = null; draft.LeaseUntil = null;
            await SaveAsync(draft, CancellationToken.None);
        }
    }

    private async Task PrepareAnalysisAsync(RepositoryOnboardingDraft draft, CancellationToken token)
    {
        if (draft.OrganizationId is null) throw new InvalidOperationException("Choose a business before profiling projects.");
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == draft.OrganizationId && x.ApplicationUserId == (draft.AnalysisRequestedByApplicationUserId ?? draft.ApplicationUserId) &&
            x.IsActive && x.ArchivedAt == null && x.PermissionLevel == OrganizationPermissionLevel.Owner, token))
            throw new UnauthorizedAccessException("Current ownership is required to profile projects.");
        var profiles = await db.ImportedProjectProfiles.Where(x => x.AnalysisDraftId == draft.Id && x.Status == "Preparing").ToListAsync(token);
        var selected = profiles.Select(x => x.ImportSourceId).ToHashSet();
        var analysisSources = await db.RepositoryOnboardingSources.Include(x => x.Draft).Where(x => selected.Contains(x.Id)).ToListAsync(token);
        foreach (var source in analysisSources)
        {
            if (source.Status == "Ready" && source.AnalysisInputJson is not null) continue;
            try
            {
                var staged = await sources.PrepareGitHubAsync(source.Draft!.ApplicationUserId, source.DraftId, source, token);
                source.Snapshot = staged.Snapshot; source.ArchiveKey = staged.ArchiveKey;
                source.AnalysisInputJson = JsonSerializer.Serialize(staged.Input, Json); source.Status = "Ready"; source.Error = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogInformation(exception, "Repository {SourceId} is unavailable for onboarding {DraftId}.", source.Id, draft.Id);
                source.Status = "Failed"; source.Error = "This repository snapshot is unavailable. Check GitHub access or retry profiling.";
                var profile = profiles.Single(x => x.ImportSourceId == source.Id);
                profile.Status = "Failed"; profile.Error = source.Error; profile.Revision++; profile.UpdatedAt = DateTimeOffset.UtcNow;
                CSweet.Infrastructure.Core.BusinessSuggestionService.Queue(db, profile.OrganizationId, AppRealtimeEvents.ProjectImportChanged, profile.ProjectId);
            }
            await SaveAsync(draft, token);
        }
        var ready = profiles.Where(x => x.Status == "Preparing").Select(x => x.ImportSourceId.ToString("D")).ToHashSet();
        var inputs = analysisSources.Where(x => x.AnalysisInputJson != null && ready.Contains(x.Id.ToString("D")))
            .Select(x => JsonSerializer.Deserialize<RepositoryAnalysisInput>(x.AnalysisInputJson!, Json)!).ToList();
        if (inputs.Count == 0) throw new InvalidOperationException("No selected project snapshot is ready. Retry profiling or enter profiles manually.");
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
            analysisSources.Single(x => x.Id.ToString("D") == input.SourceId).AnalysisInputJson = JsonSerializer.Serialize(input, Json);
        var installation = await assistant.RequireInstallationAsync(draft.AnalysisRequestedByApplicationUserId ?? draft.ApplicationUserId, token);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        var work = await inbox.EnqueueAsync(PersonalAssistantCapabilities.UserScope(draft.AnalysisRequestedByApplicationUserId ?? draft.ApplicationUserId), installation,
            AgentWorkKind.Capability, PersonalAssistantCapabilities.Analyze,
            JsonSerializer.SerializeToElement(new AnalyzeRepositoryRequest(inputs), Json),
            $"repository-analysis:{draft.Id:N}:{draft.SourceRevision}:{draft.Revision}", DateTimeOffset.UtcNow.AddMinutes(30),
            sourceType: "repository-onboarding-analysis", sourceId: draft.Id.ToString("D"), cancellationToken: token);
        foreach (var profile in profiles.Where(x => ready.Contains(x.ImportSourceId.ToString("D"))))
        { profile.Status = "Analyzing"; profile.AgentWorkItemId = work.Id; profile.Revision++; profile.AnalysisRevision = profile.Revision; CSweet.Infrastructure.Core.BusinessSuggestionService.Queue(db, profile.OrganizationId, AppRealtimeEvents.ProjectImportChanged, profile.ProjectId); }
        draft.AgentWorkItemId = work.Id; draft.Status = "Analyzing";
        await SaveAsync(draft, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        try { await scheduler.WakeAsync(installation, token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogInformation(exception, "Profiling work {WorkId} is saved; runtime wake will be recovered by normal inbox maintenance.", work.Id); }
    }

    private Task CreateBusinessAsync(RepositoryOnboardingDraft draft, CancellationToken token) =>
        throw new InvalidOperationException("Resume this draft from its business import wizard to add projects. Business creation is independent of repository import.");

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

    internal async Task AttachGitHubAsync(RepositoryOnboardingDraft draft, RepositoryOnboardingSource source, CancellationToken token)
    {
        var descriptor = await CheckGitHubAsync(draft, source, token);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
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
        var existing = await db.SourceControlRepositories.SingleOrDefaultAsync(x => x.OrganizationId == business && x.ProviderRepositoryKey == "github:" + descriptor.RepositoryId, token);
        if (existing is not null) source.RepositoryId = existing.Id;
        if (existing is null && !await db.SourceControlRepositories.AnyAsync(x => x.Id == source.RepositoryId, token))
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

    internal async Task ImportZipAsync(RepositoryOnboardingDraft draft, RepositoryOnboardingSource source, CancellationToken token)
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
