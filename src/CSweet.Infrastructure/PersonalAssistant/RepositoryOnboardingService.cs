using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.SourceControl;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Domain.PersonalAssistant;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.PersonalAssistant;

public sealed partial class RepositoryOnboardingService(CSweetDbContext db, IRepositorySourceStore sources,
    ISourceControlPlatformConfigurationProvider platform, ITrustedSourceControlHostClient host,
    IGitHubUserAuthorizationClient githubUsers, IPersonalAssistantService assistant, AgentWorkInbox inbox,
    RepositoryOnboardingWorkflow workflow) : IRepositoryOnboardingService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RepositoryOnboardingDraftResponse> CreateAsync(Guid userId, CancellationToken token = default)
    {
        if (!await db.Users.AnyAsync(x => x.Id == userId, token)) throw new UnauthorizedAccessException("Sign in to create an onboarding draft.");
        var now = DateTimeOffset.UtcNow;
        var draft = new RepositoryOnboardingDraft { Id = Guid.NewGuid(), ApplicationUserId = userId, CreatedAt = now, UpdatedAt = now };
        db.RepositoryOnboardingDrafts.Add(draft); await SaveAsync(draft, token);
        return Response(draft);
    }
    public async Task<IReadOnlyList<RepositoryOnboardingDraftResponse>> ListAsync(Guid userId, CancellationToken token = default) =>
        (await db.RepositoryOnboardingDrafts.AsNoTracking().Include(x => x.Sources).Where(x => x.ApplicationUserId == userId &&
            x.Status != "Discarded" && x.Status != "Created").OrderByDescending(x => x.UpdatedAt).Take(20).ToListAsync(token)).Select(Response).ToArray();
    public async Task<RepositoryOnboardingDraftResponse> GetAsync(Guid userId, Guid id, CancellationToken token = default) => Response(await RequireAsync(userId, id, token));

    public async Task<DraftGitHubAuthorization> AuthorizeGitHubAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft);
        if (!(await platform.GetReadinessAsync(token)).ExistingGitHubAvailable)
            throw new InvalidOperationException("Set up GitHub for this C-Sweet installation, or import a repository ZIP.");
        var state = $"repository-onboarding:{draft.Id:D}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        draft.GitHubStateHash = Hash(state); draft.GitHubStateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await SaveAsync(draft, token);
        var url = await platform.GetInstallUrlAsync(CSweet.Domain.Setup.PlatformGitHubAppKind.SourceAccess, token);
        return new(url + (url.Contains('?') ? "&" : "?") + "state=" + Uri.EscapeDataString(state), state);
    }

    public async Task<RepositoryOnboardingDraftResponse> CompleteGitHubAsync(Guid userId, Guid id,
        CompleteDraftGitHubAuthorizationRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft);
        if (string.IsNullOrWhiteSpace(request.State) || request.State.Length > 512 || string.IsNullOrWhiteSpace(request.Code) ||
            request.Code.Length > 2048 || request.InstallationId <= 0)
            throw new ArgumentException("The GitHub response is invalid.");
        if (draft.GitHubStateExpiresAt <= DateTimeOffset.UtcNow || draft.GitHubStateExpiresAt is null ||
            draft.GitHubStateHash.Length != 64 || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(draft.GitHubStateHash), Convert.FromHexString(Hash(request.State))))
            throw new UnauthorizedAccessException("The GitHub return did not match your current onboarding draft.");
        var config = await platform.GetUserAuthorizationAsync(CSweet.Domain.Setup.PlatformGitHubAppKind.SourceAccess, token);
        await githubUsers.VerifyInstallationAsync(config, request.Code, request.InstallationId, token);
        var installation = await host.DescribeInstallationAsync(request.InstallationId, token);
        if (installation.Suspended) throw new InvalidOperationException("This GitHub installation is suspended. Restore it in GitHub before continuing.");
        var accounts = JsonSerializer.Deserialize<List<long>>(draft.VerifiedInstallationsJson) ?? [];
        if (!accounts.Contains(request.InstallationId)) accounts.Add(request.InstallationId);
        draft.VerifiedInstallationsJson = JsonSerializer.Serialize(accounts);
        draft.GitHubStateExpiresAt = null; await SaveAsync(draft, token);
        return Response(draft);
    }

    public async Task<IReadOnlyList<OnboardingGitHubAccount>> AccountsAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); var result = new List<OnboardingGitHubAccount>();
        foreach (var account in JsonSerializer.Deserialize<long[]>(draft.VerifiedInstallationsJson) ?? [])
        {
            var item = await host.DescribeInstallationAsync(account, token);
            if (!item.Suspended) result.Add(new(account, item.AccountLogin, item.AccountType));
        }
        return result;
    }
    public async Task<IReadOnlyList<OnboardingGitHubRepository>> RepositoriesAsync(Guid userId, Guid id, long installationId, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); await RequireAccountAsync(draft, installationId, token);
        var available = await host.ListRepositoriesAsync(installationId, token);
        var connected = await db.SourceControlRepositories.AsNoTracking().Where(x => x.OrganizationId != draft.OrganizationId)
            .Select(x => x.ProviderRepositoryKey).ToListAsync(token);
        return available.Take(1000).Select(x => new OnboardingGitHubRepository(x.RepositoryId, x.FullName, x.IsPrivate, x.IsArchived,
            !x.IsArchived && !connected.Contains("github:" + x.RepositoryId), x.IsArchived ? "Archived in GitHub" :
                connected.Contains("github:" + x.RepositoryId) ? "Already associated with another C-Sweet business" : null)).ToArray();
    }

    public async Task<RepositoryOnboardingDraftResponse> SelectAsync(Guid userId, Guid id,
        SelectOnboardingRepositoriesRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft, request.ExpectedRevision);
        await RequireAccountAsync(draft, request.InstallationId, token);
        if (request.RepositoryIds.Count is < 1 or > 50 || request.RepositoryIds.Distinct().Count() != request.RepositoryIds.Count)
            throw new ArgumentException("Choose between one and fifty distinct repositories.");
        var available = await host.ListRepositoriesAsync(request.InstallationId, token);
        foreach (var repositoryId in request.RepositoryIds)
        {
            var repository = available.SingleOrDefault(x => x.RepositoryId == repositoryId && !x.IsArchived)
                ?? throw new UnauthorizedAccessException("A selected repository is not available to your GitHub installation.");
            if (await db.SourceControlRepositories.AnyAsync(x => x.ProviderRepositoryKey == "github:" + repositoryId && x.OrganizationId != draft.OrganizationId, token))
                throw new InvalidOperationException("A selected repository already belongs to another C-Sweet business.");
            if (draft.Sources.Any(x => x.ExternalRepositoryId == repositoryId)) continue;
            var source = new RepositoryOnboardingSource
            {
                Id = Guid.NewGuid(),
                DraftId = draft.Id,
                Name = repository.Name,
                Owner = repository.Owner,
                DefaultBranch = repository.DefaultBranch,
                IsPrivate = repository.IsPrivate,
                InstallationId = request.InstallationId,
                ExternalRepositoryId = repositoryId,
                RepositoryId = Guid.NewGuid()
            };
            draft.Sources.Add(source);
            db.RepositoryOnboardingSources.Add(source);
        }
        if (draft.Sources.Count > 50) throw new ArgumentException("Choose no more than fifty repositories for one business.");
        InvalidateAnalysis(draft); await SaveAsync(draft, token); return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> UploadAsync(Guid userId, Guid id, long expectedRevision,
        string fileName, Stream archive, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft, expectedRevision);
        if (draft.Sources.Count >= 50) throw new ArgumentException("Choose no more than fifty repositories.");
        var sourceId = Guid.NewGuid(); var staged = await sources.StageUploadAsync(userId, id, sourceId, fileName, archive, token);
        if (draft.Sources.Any(x => x.ArchiveKey == staged.ArchiveKey && x.Kind == "Zip")) return Response(draft);
        var source = new RepositoryOnboardingSource
        {
            Id = sourceId,
            DraftId = id,
            Kind = "Zip",
            Name = staged.Name,
            IsPrivate = true,
            Status = "Ready",
            Snapshot = staged.Snapshot,
            ArchiveKey = staged.ArchiveKey,
            AnalysisInputJson = JsonSerializer.Serialize(staged.Input, Json),
            RepositoryId = Guid.NewGuid()
        };
        draft.Sources.Add(source);
        db.RepositoryOnboardingSources.Add(source);
        InvalidateAnalysis(draft); await SaveAsync(draft, token); return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> RemoveAsync(Guid userId, Guid id, Guid sourceId, long expectedRevision, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft, expectedRevision);
        var source = draft.Sources.SingleOrDefault(x => x.Id == sourceId) ?? throw new KeyNotFoundException("The source was not found.");
        if (source.Attached) throw new InvalidOperationException("Manage attached repositories from the business Source Control page.");
        db.RepositoryOnboardingSources.Remove(source); draft.Sources.Remove(source);
        InvalidateAnalysis(draft); await SaveAsync(draft, token); return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> AnalyzeAsync(Guid userId, Guid id, AnalyzeOnboardingRepositoriesRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft, request.ExpectedRevision);
        if (!request.ReadAccessApproved) throw new ArgumentException("Authorize the assistant to read the selected repository snapshots.");
        if (draft.Sources.Count == 0) throw new ArgumentException("Choose at least one repository.");
        await assistant.RequireInstallationAsync(userId, token);
        draft.Status = "Preparing"; draft.Error = null; await SaveAsync(draft, token); return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> SaveReviewAsync(Guid userId, Guid id,
        SaveRepositoryBusinessReviewRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        if (draft.Status == "Analyzing")
        { if (draft.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The draft changed. Reload before saving."); }
        else RequireEditable(draft, request.ExpectedRevision);
        RepositoryBriefValidator.ValidateReview(request.Review);
        var inputs = Inputs(draft);
        RepositoryBriefValidator.ValidateEvidence(request.Review.Projects, inputs);
        if (Encoding.UTF8.GetByteCount(RepositoryOnboardingWorkflow.BuildDocument(draft, request.Review)) > 131072)
            throw new ArgumentException("Shorten the combined brief so it fits the 128 KiB document limit.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        if (draft.Status == "Analyzing" && draft.AgentWorkItemId is { } work) await inbox.CancelAsync(work, "The owner chose manual review.", token);
        draft.ReviewJson = JsonSerializer.Serialize(request.Review, Json); draft.Status = "Review"; draft.Error = null;
        await SaveAsync(draft, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> ConfirmAsync(Guid userId, Guid id,
        ConfirmRepositoryBusinessRequest request, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        if (draft.Status is "Creating" or "Created") return Response(draft);
        RequireEditable(draft, request.ExpectedRevision);
        if (!request.RepositoryManagementApproved) throw new ArgumentException("Confirm the selected repository associations before creating the business.");
        if (draft.Sources.Count == 0 || draft.Sources.Any(x => x.Status != "Ready"))
            throw new InvalidOperationException("Remove or retry unavailable sources before creating the business.");
        var review = JsonSerializer.Deserialize<RepositoryBusinessReview>(draft.ReviewJson, Json)
            ?? throw new ArgumentException("Review the company details first.");
        RepositoryBriefValidator.ValidateReview(review); RepositoryBriefValidator.ValidateEvidence(review.Projects, Inputs(draft));
        draft.Status = "Creating"; draft.Error = null; await SaveAsync(draft, token); return Response(draft);
    }

    public async Task<RepositoryOnboardingDraftResponse> RetryAsync(Guid userId, Guid id, long expectedRevision, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token); RequireEditable(draft, expectedRevision);
        if (draft.Status is not ("AnalysisFailed" or "CreationFailed"))
            throw new InvalidOperationException("Only an unsuccessful saved step may be retried.");
        draft.Status = draft.OrganizationId.HasValue || draft.Status == "CreationFailed" ? "Creating" : "Preparing";
        draft.Error = null; await SaveAsync(draft, token); return Response(draft);
    }
    public async Task DiscardAsync(Guid userId, Guid id, long expectedRevision, CancellationToken token = default)
    {
        var draft = await RequireAsync(userId, id, token);
        if (draft.Revision != expectedRevision) throw new DbUpdateConcurrencyException("The draft changed. Reload before discarding it.");
        if (draft.OrganizationId.HasValue || draft.Status == "Creating" || draft.Status == "Preparing")
            throw new InvalidOperationException("Wait for the current operation before discarding. An already-created business is managed from Enterprise.");
        if (draft.AgentWorkItemId is { } work) await inbox.CancelAsync(work, "The owner discarded the draft.", token);
        draft.Status = "Discarded"; draft.ReviewJson = "{}"; draft.AnalysisJson = "{}";
        foreach (var source in draft.Sources) source.AnalysisInputJson = null;
        await SaveAsync(draft, token); await sources.DiscardAsync(userId, id, token);
    }

    public async Task<bool> ProcessNextAsync(string workerId, CancellationToken token = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var query = db.RepositoryOnboardingDrafts.Where(x => (x.Status == "Preparing" || x.Status == "Creating") &&
            (x.LeaseUntil == null || x.LeaseUntil < now)).OrderBy(x => x.UpdatedAt).Take(1);
        var draft = db.Database.IsNpgsql()
            ? await db.RepositoryOnboardingDrafts.FromSqlInterpolated($"SELECT * FROM \"RepositoryOnboardingDrafts\" WHERE \"Status\" IN ('Preparing', 'Creating') AND (\"LeaseUntil\" IS NULL OR \"LeaseUntil\" < {now}) ORDER BY \"UpdatedAt\" FOR UPDATE SKIP LOCKED LIMIT 1").SingleOrDefaultAsync(token)
            : await query.SingleOrDefaultAsync(token);
        if (draft is null) return false;
        draft.LeaseOwner = workerId; draft.LeaseUntil = now.AddMinutes(30); await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        await db.Entry(draft).Collection(x => x.Sources).LoadAsync(token);
        await workflow.ProcessAsync(draft, token);
        return true;
    }

    private async Task<RepositoryOnboardingDraft> RequireAsync(Guid userId, Guid id, CancellationToken token) =>
        await db.RepositoryOnboardingDrafts.Include(x => x.Sources).SingleOrDefaultAsync(x => x.Id == id && x.ApplicationUserId == userId && x.Status != "Discarded", token)
        ?? throw new KeyNotFoundException("The onboarding draft was not found.");
    private static void RequireEditable(RepositoryOnboardingDraft draft, long? revision = null)
    {
        if (revision.HasValue && revision != draft.Revision) throw new DbUpdateConcurrencyException("The draft changed. Reload before continuing.");
        if (draft.Status is "Preparing" or "Analyzing" or "Creating" or "Created")
            throw new InvalidOperationException("Wait for the current operation before changing the draft.");
    }
    private async Task RequireAccountAsync(RepositoryOnboardingDraft draft, long id, CancellationToken token)
    {
        if (!(JsonSerializer.Deserialize<long[]>(draft.VerifiedInstallationsJson) ?? []).Contains(id)) throw new UnauthorizedAccessException("Authorize this GitHub account for your draft first.");
        if ((await host.DescribeInstallationAsync(id, token)).Suspended) throw new InvalidOperationException("The GitHub installation is suspended.");
    }
    internal static IReadOnlyList<RepositoryAnalysisInput> Inputs(RepositoryOnboardingDraft draft) => draft.Sources
        .Where(x => x.AnalysisInputJson is not null).Select(x => JsonSerializer.Deserialize<RepositoryAnalysisInput>(x.AnalysisInputJson!, Json)!).ToArray();
    private static void InvalidateAnalysis(RepositoryOnboardingDraft draft)
    { draft.SourceRevision++; draft.Status = "Sources"; draft.AnalysisJson = "{}"; draft.ReviewJson = "{}"; draft.Error = null; draft.AgentWorkItemId = null; }
    private async Task SaveAsync(RepositoryOnboardingDraft draft, CancellationToken token)
    { draft.Revision++; draft.UpdatedAt = DateTimeOffset.UtcNow; PersonalAssistantRealtime.Queue(db, draft.ApplicationUserId, AppRealtimeEvents.RepositoryOnboardingChanged, draft.Id); await db.SaveChangesAsync(token); }
    internal static RepositoryOnboardingDraftResponse Response(RepositoryOnboardingDraft x) => new(x.Id, x.Status, x.Revision,
        x.SourceRevision, x.Sources.Select(s => new RepositorySourceResponse(s.Id, s.Kind, s.Owner.Length > 0 ? s.Owner + "/" + s.Name : s.Name,
            s.Status, s.Snapshot, s.Error, s.Attached)).ToArray(),
        x.ReviewJson == "{}" ? null : JsonSerializer.Deserialize<RepositoryBusinessReview>(x.ReviewJson, Json),
        x.AnalysisJson == "{}" ? null : JsonSerializer.Deserialize<RepositoryCompanyBrief>(x.AnalysisJson, Json),
        x.OrganizationId, x.BriefArtifactId, x.Error, x.ActionUri, x.UpdatedAt);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
