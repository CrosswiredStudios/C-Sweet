using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

/// <summary>Prevents agents manufacturing visible attachment provenance from guessed organization asset IDs.</summary>
public sealed class AgentAttachmentAccessService(CSweetDbContext db)
{
    public async Task RequireAsync(OrganizationUser actor, IEnumerable<MediaAsset> assets, CancellationToken ct)
    {
        if (actor.EmployeeType != EmployeeType.Agent) return;
        var requested = assets.ToArray();
        if (requested.Length == 0) return;
        if (!actor.IsActive || actor.AgentInstallationId is null)
            throw Denied();
        var organization = actor.OrganizationId.ToString("D");
        var grants = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == actor.AgentInstallationId &&
                x.BusinessId == organization && x.IsEnabled && x.SetupState == PluginSetupState.Ready &&
                x.RevisionStatus == PluginRevisionStatus.Active)
            .Select(x => x.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(ct);
        if (grants is null) throw Denied();
        var canReadChats = (JsonSerializer.Deserialize<string[]>(grants) ?? []).Contains(CommunicationCapabilities.ChatRead, StringComparer.Ordinal);
        foreach (var asset in requested)
        {
            if (asset.OrganizationId != actor.OrganizationId) throw Denied();
            var retained = await (from attachment in db.ConversationMessageAttachments.AsNoTracking()
                join message in db.CoreConversationMessages.AsNoTracking() on attachment.MessageId equals message.Id
                join chat in db.CoreConversations.AsNoTracking() on attachment.ConversationId equals chat.Id
                where attachment.OrganizationId == actor.OrganizationId && attachment.MediaAssetId == asset.Id &&
                    attachment.Sha256 == asset.Sha256 && attachment.SizeBytes == asset.SizeBytes && attachment.ContentType == asset.ContentType &&
                    message.ConversationId == chat.Id && chat.OrganizationId == actor.OrganizationId && chat.ArchivedAt == null &&
                    chat.Participants.Any(p => p.OrganizationUserId == actor.Id && p.LeftAt == null)
                select attachment.Id).AnyAsync(ct);
            if (retained && canReadChats) continue;
            if (asset.WorkstreamId is { } workstreamId)
            {
                var project = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == workstreamId && x.OrganizationId == actor.OrganizationId, ct);
                if (project is null) throw Denied();
                var teams = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x =>
                    x.WorkstreamId == workstreamId && x.EndsAt == null).Select(x => x.TeamId).ToListAsync(ct);
                if (project.AccountableManagerOrganizationUserId == actor.Id ||
                    await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x => x.WorkstreamId == workstreamId &&
                        x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null, ct) ||
                    await db.TeamMemberships.AsNoTracking().AnyAsync(x => teams.Contains(x.TeamId) &&
                        x.OrganizationUserId == actor.Id && x.EndedAt == null, ct)) continue;
                throw Denied();
            }
            if (asset.CreatingAgentInstallationId == actor.AgentInstallationId || asset.GenAiJobId is { } jobId &&
                await db.GenAiJobs.AsNoTracking().AnyAsync(x => x.Id == jobId && x.OrganizationId == actor.OrganizationId &&
                    x.AgentInstallationId == actor.AgentInstallationId && x.WorkstreamId == null, ct)) continue;
            throw Denied();
        }
    }

    private static UnauthorizedAccessException Denied() => new("The attachment is not accessible to this employee.");
}
