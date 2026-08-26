using CSweet.Application.Communications;
using CSweet.Application.GenAi;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class MediaAssetConversationAttachmentSourceResolver(
    CSweetDbContext db,
    IMediaAssetService mediaAssets) : IConversationAttachmentSourceResolver
{
    public string Source => "csweet-media";

    public async Task<ResolvedConversationAttachment?> ResolveAsync(
        Guid organizationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var attachment = await db.ConversationMessageAttachments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.OrganizationId == organizationId,
                cancellationToken);
        if (attachment is null) return null;
        var opened = await mediaAssets.OpenReadAsync(
            attachment.MediaAssetId, organizationId, cancellationToken);
        if (opened is null) return null;
        return new ResolvedConversationAttachment(
            new CommunicationMessageAttachmentResponse(
                attachment.Id, attachment.MessageId, attachment.FileName, attachment.ContentType,
                attachment.SizeBytes, attachment.Sha256),
            attachment.ConversationId,
            attachment.MediaAssetId,
            opened.Value.Content);
    }
}
