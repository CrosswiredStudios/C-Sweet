using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Resolves retained media provenance without opening bytes or authorizing an external effect.</summary>
public sealed class ConnectorMediaSourceService(CSweetDbContext db)
{
    public async Task<ConnectorMediaBinding> ResolveAsync(Guid organizationId, Guid requesterId, Guid assetId,
        ConversationAttachmentReference? source, CancellationToken ct)
    {
        if (assetId == Guid.Empty || source is null || source.ConversationId == Guid.Empty ||
            source.MessageId == Guid.Empty || source.AttachmentId == Guid.Empty)
            throw Denied();
        var organization = organizationId.ToString("D");
        var grants = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == requesterId &&
                x.BusinessId == organization && x.IsEnabled && x.SetupState == PluginSetupState.Ready &&
                x.RevisionStatus == PluginRevisionStatus.Active)
            .Select(x => x.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(ct);
        if (!(JsonSerializer.Deserialize<string[]>(grants ?? "[]") ?? [])
            .Contains(CommunicationCapabilities.ChatRead, StringComparer.Ordinal))
            throw Denied();
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == requesterId &&
            x.IsActive && x.EmployeeType == EmployeeType.Agent, ct);
        if (actor is null || !await db.CoreConversations.AsNoTracking().AnyAsync(x =>
            x.Id == source.ConversationId && x.OrganizationId == organizationId && x.ArchivedAt == null &&
            x.Participants.Any(p => p.OrganizationUserId == actor.Id && p.LeftAt == null), ct))
            throw Denied();
        var attachment = await db.ConversationMessageAttachments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == source.AttachmentId && x.OrganizationId == organizationId && x.ConversationId == source.ConversationId &&
            x.MessageId == source.MessageId && x.MediaAssetId == assetId, ct);
        if (attachment is null || !await db.CoreConversationMessages.AsNoTracking().AnyAsync(x =>
            x.Id == source.MessageId && x.ConversationId == source.ConversationId, ct))
            throw Denied();
        var asset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == assetId && x.OrganizationId == organizationId, ct);
        if (asset is null || asset.SizeBytes <= 0 || asset.SizeBytes > 256L * 1024 * 1024 * 1024 ||
            asset.Sha256.Length != 64 || !asset.Sha256.All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(asset.ContentType) || asset.ContentType.Length > 255 ||
            string.IsNullOrWhiteSpace(attachment.FileName) || attachment.FileName.Length > 512 ||
            asset.Sha256 != attachment.Sha256 || asset.SizeBytes != attachment.SizeBytes ||
            asset.ContentType != attachment.ContentType)
            throw Denied();
        return new(asset.Id, asset.Sha256, asset.SizeBytes, asset.ContentType, source, attachment.FileName);
    }

    private static UnauthorizedAccessException Denied() =>
        new("The retained media attachment is unavailable or not accessible to this installation.");
}
