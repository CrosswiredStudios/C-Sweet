using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.PersonalAssistant;

public sealed class PersonalAssistantResultObserver(CSweetDbContext db) : IAgentWorkResultObserver
{
    public async Task ObserveAsync(AgentWorkItem work, bool succeeded, JsonElement? value, CancellationToken token)
    {
        if (work.SourceType == "personal-assistant-message" && Guid.TryParse(work.SourceId, out var messageId))
        {
            var message = await db.PersonalAssistantMessages.SingleOrDefaultAsync(x => x.Id == messageId && x.AgentWorkItemId == work.Id, token);
            if (message is null || message.Status != "Pending" || work.OrganizationId != PersonalAssistantCapabilities.UserScope(message.ApplicationUserId)) return;
            AssistantConversationResponse? response = null;
            try { if (succeeded && value.HasValue) response = value.Value.Deserialize<AssistantConversationResponse>(RepositoryOnboardingService.Json); }
            catch (JsonException) { }
            message.Status = response is not null && !string.IsNullOrWhiteSpace(response.Message) && response.Message.Length <= 32000 ? "Completed" : "Failed";
            message.Content = message.Status == "Completed" ? response!.Message : "Your assistant could not finish this response. Check its model settings or try again.";
            PersonalAssistantRealtime.Queue(db, message.ApplicationUserId, AppRealtimeEvents.PersonalAssistantChanged, message.Id);
        }
        if (work.SourceType != "repository-onboarding-analysis" || !Guid.TryParse(work.SourceId, out var id)) return;
        var draft = await db.RepositoryOnboardingDrafts.Include(x => x.Sources).SingleOrDefaultAsync(x => x.Id == id && x.AgentWorkItemId == work.Id, token);
        if (draft is null || draft.Status != "Analyzing" || work.OrganizationId != PersonalAssistantCapabilities.UserScope(draft.AnalysisRequestedByApplicationUserId ?? draft.ApplicationUserId)) return;
        try
        {
            var result = succeeded && value.HasValue ? value.Value.Deserialize<RepositoryCompanyBrief>(RepositoryOnboardingService.Json) : null;
            if (result is null) throw new ArgumentException("The agent did not return a brief.");
            var review = new RepositoryBusinessReview(result.BusinessName, result.Industry, result.Mission, "", result.Summary, result.Projects, result.OpenQuestions);
            // An unknown company name is editable; it must not make the entire analysis unrecoverable.
            var forValidation = review with { BusinessName = string.IsNullOrWhiteSpace(review.BusinessName) ? "Unspecified" : review.BusinessName };
            RepositoryBriefValidator.ValidateReview(forValidation);
            var pendingProfiles = await db.ImportedProjectProfiles.Where(x => x.AgentWorkItemId == work.Id && x.OrganizationId == draft.OrganizationId).ToListAsync(token);
            if (draft.OrganizationId is null || !await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == draft.OrganizationId &&
                x.ApplicationUserId == (draft.AnalysisRequestedByApplicationUserId ?? draft.ApplicationUserId) && x.IsActive && x.ArchivedAt == null &&
                x.EmployeeType == CSweet.Domain.Core.EmployeeType.Human && x.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Owner, token))
                throw new ArgumentException("The profiling request is no longer authorized.");
            var expectedSources = pendingProfiles.Select(x => x.ImportSourceId).ToArray();
            var inputs = await db.RepositoryOnboardingSources.Where(x => expectedSources.Contains(x.Id)).Select(x => x.AnalysisInputJson).ToListAsync(token);
            RepositoryBriefValidator.ValidateEvidence(result.Projects, inputs.Where(x => x != null).Select(x => JsonSerializer.Deserialize<RepositoryAnalysisInput>(x!, RepositoryOnboardingService.Json)!).ToArray());
            if (result.Projects.Any(x => x.Category is not ("Unknown" or "Software" or "VideoGame" or "Other") || x.Uncertainties is null || x.Uncertainties.Count > 10 || x.Uncertainties.Any(u => u is null || u.Length > 2000))) throw new ArgumentException("The project category is unsupported.");
            foreach (var projectProfile in pendingProfiles.Where(x => x.Status == "Analyzing" && x.Revision == x.AnalysisRevision))
            {
                var brief = result.Projects.Single(x => x.SourceId == projectProfile.ImportSourceId.ToString("D"));
                projectProfile.InferredJson = JsonSerializer.Serialize(brief, RepositoryOnboardingService.Json);
                projectProfile.Status = "Review"; projectProfile.Error = null; projectProfile.Revision++; projectProfile.UpdatedAt = DateTimeOffset.UtcNow;
                CSweet.Infrastructure.Core.BusinessSuggestionService.Queue(db, projectProfile.OrganizationId, AppRealtimeEvents.ProjectImportChanged, projectProfile.ProjectId);
            }
            draft.AnalysisJson = JsonSerializer.Serialize(result, RepositoryOnboardingService.Json);
            draft.ReviewJson = JsonSerializer.Serialize(review, RepositoryOnboardingService.Json);
            draft.Status = "Review"; draft.Error = null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NullReferenceException)
        {
            draft.Status = "AnalysisFailed"; draft.Error = "The assistant could not produce supported profiles. Your projects are saved. Retry or enter the details manually.";
            foreach (var profile in await db.ImportedProjectProfiles.Where(x => x.AgentWorkItemId == work.Id && x.Status == "Analyzing").ToListAsync(token))
            { profile.Status = "Failed"; profile.Error = draft.Error; profile.Revision++; profile.UpdatedAt = DateTimeOffset.UtcNow;
                CSweet.Infrastructure.Core.BusinessSuggestionService.Queue(db, profile.OrganizationId, AppRealtimeEvents.ProjectImportChanged, profile.ProjectId); }
        }
        draft.Revision++; draft.UpdatedAt = DateTimeOffset.UtcNow;
        PersonalAssistantRealtime.Queue(db, draft.ApplicationUserId, AppRealtimeEvents.RepositoryOnboardingChanged, draft.Id);
    }
}
