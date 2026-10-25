using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    // Only the original active human gets a review control. Existing memory endpoints
    // independently recheck audience authority, holds, current impact and exact decisions.
    private async Task<IReadOnlyList<PluginCleanupReview>> CleanupReviews(Guid user,Guid installation,CancellationToken ct,Guid[] visibleAccounts)
    {
        var query=from copy in db.PluginAccountDataCopies.AsNoTracking()
            join account in db.PluginAccountGroups.AsNoTracking() on copy.AccountGroupId equals account.Id
            join message in db.CoreConversationMessages.AsNoTracking() on copy.RecordId equals message.Id
            join conversation in db.CoreConversations.AsNoTracking() on message.ConversationId equals conversation.Id
            join human in db.CoreOrganizationUsers.AsNoTracking() on conversation.InitiatedByOrganizationUserId equals human.Id
            where account.PluginInstallationId==installation && visibleAccounts.Contains(account.Id) && account.State==PluginAccountState.Disconnecting && (copy.Store==nameof(ConversationMessage) || copy.Store=="memory-origin") &&
                copy.OrganizationId==conversation.OrganizationId && human.ApplicationUserId==user && human.EmployeeType==EmployeeType.Human && human.IsActive &&
                human.ArchivedAt==null && conversation.AgentOrganizationUserId!=null &&
                db.MemoryCaptureOutbox.Any(j=>j.ConversationMessageId==message.Id && j.EpisodeCapturedAt!=null) &&
                !db.MemoryErasureReceipts.Any(r=>r.OrganizationId==conversation.OrganizationId && r.EpisodeId==message.Id)
            select new PluginCleanupReview(account.Id,conversation.OrganizationId,conversation.AgentOrganizationUserId!.Value,message.Id);
        var episodes=from copy in db.PluginAccountDataCopies.AsNoTracking()
            join account in db.PluginAccountGroups.AsNoTracking() on copy.AccountGroupId equals account.Id
            join job in db.MemoryEpisodeEnrichmentJobs.AsNoTracking() on copy.RecordId equals job.EpisodeId
            where account.PluginInstallationId==installation && visibleAccounts.Contains(account.Id) && account.State==PluginAccountState.Disconnecting && copy.Store=="memory-origin" &&
                copy.OrganizationId==job.OrganizationId && job.ReviewerApplicationUserId==user &&
                db.CoreOrganizationUsers.Any(u=>u.Id==job.EmployeeId && u.OrganizationId==job.OrganizationId && u.AgentInstallationId==copy.ConsumerInstallationId && u.IsActive && u.ArchivedAt==null) &&
                !db.MemoryErasureReceipts.Any(r=>r.OrganizationId==job.OrganizationId && r.EpisodeId==job.EpisodeId)
            select new PluginCleanupReview(account.Id,job.OrganizationId,job.EmployeeId,job.EpisodeId);
        return (await query.Distinct().Take(64).ToArrayAsync(ct)).Concat(await episodes.Distinct().Take(64).ToArrayAsync(ct)).Distinct().Take(64).ToArray();
    }
}
