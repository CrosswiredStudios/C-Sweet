using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    internal sealed record MentionInput(Guid Id, Guid PersonId, string Hash);
    internal sealed record AttachmentInput(Guid Id, Guid AssetId, string Hash);
    internal sealed record AuxiliaryEvidence(int Version, Guid SenderId, string SenderHash, MentionInput[] Mentions,
        AttachmentInput[] Attachments, string ContextHash, string AttachmentsHash, string? WorkContextHash, string? WorkstreamSourceHash);
    private sealed record FrozenMetadata(PreparedChatMetadata Metadata, AuxiliaryEvidence Evidence);

    private static string MetadataHash<T>(T value) => Hash(JsonSerializer.Serialize(value, Json));
    private static string ContextHash(IReadOnlyDictionary<string, string> values) => MetadataHash(
        new SortedDictionary<string, string>(values.ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal));

    private async Task<FrozenMetadata> CaptureMetadataAsync(Binding binding, CancellationToken token)
    {
        var conversation = await db.CoreConversations.AsNoTracking().SingleAsync(x => x.Id == binding.ConversationId, token);
        var message = await db.CoreConversationMessages.AsNoTracking().SingleAsync(x => x.Id == binding.MessageId, token);
        var mentions = await db.ConversationMessageMentions.AsNoTracking().Where(x => x.MessageId == binding.MessageId)
            .OrderBy(x => x.Offset).ThenBy(x => x.Id).Take(101).ToArrayAsync(token);
        var attachments = await db.ConversationMessageAttachments.AsNoTracking().Where(x => x.MessageId == binding.MessageId)
            .OrderBy(x => x.Id).Take(33).ToArrayAsync(token);
        if (mentions.Length > 100 || attachments.Length > 32 ||
            mentions.Any(x => x.OrganizationId != binding.OrganizationId || x.ConversationId != binding.ConversationId ||
                x.Offset < 0 || x.Length < 1 || (long)x.Offset + x.Length > message.Content.Length) ||
            attachments.Any(x => x.OrganizationId != binding.OrganizationId || x.ConversationId != binding.ConversationId ||
                x.SizeBytes < 0 || !Digest(x.Sha256))) throw Denied();
        var peopleIds = mentions.Select(x => x.MentionedOrganizationUserId).Append(binding.HumanId).Distinct().ToArray();
        var people = await db.CoreOrganizationUsers.AsNoTracking().Include(x => x.Role).Where(x => peopleIds.Contains(x.Id) &&
            x.OrganizationId == binding.OrganizationId && x.IsActive && x.ArchivedAt == null).ToDictionaryAsync(x => x.Id, token);
        if (people.Count != peopleIds.Length || people.Values.Any(x => x.Role is { } role && role.OrganizationId != binding.OrganizationId)) throw Denied();
        string PersonHash(OrganizationUser person) => MetadataHash(new { person.Id, person.OrganizationId, person.DisplayName,
            person.EmployeeType, person.RoleId, RoleName = person.Role?.Name, person.Revision, person.IsActive, person.ArchivedAt });
        var sender = people[binding.HumanId];
        var renderedSender = new PreparedChatSender(sender.Id, sender.DisplayName, sender.EmployeeType.ToString(), sender.Role?.Name);
        var renderedMentions = mentions.Select(x => new PreparedChatMention(x.MentionedOrganizationUserId,
            people[x.MentionedOrganizationUserId].DisplayName, people[x.MentionedOrganizationUserId].EmployeeType.ToString(), x.Offset, x.Length)).ToArray();
        var mentionInputs = mentions.Select(x => new MentionInput(x.Id, x.MentionedOrganizationUserId,
            MetadataHash(new { x.Id, x.OrganizationId, x.ConversationId, x.MessageId, x.MentionedOrganizationUserId,
                x.Offset, x.Length, x.DisplayText, x.RecipientWasParticipant, x.CreatedAt, PersonHash = PersonHash(people[x.MentionedOrganizationUserId]) }))).ToArray();
        var assetIds = attachments.Select(x => x.MediaAssetId).Distinct().ToArray();
        var assets = await db.MediaAssets.AsNoTracking().Where(x => assetIds.Contains(x.Id) && x.OrganizationId == binding.OrganizationId)
            .ToDictionaryAsync(x => x.Id, token);
        if (assets.Count != assetIds.Length) throw Denied();
        var attachmentInputs = attachments.Select(x =>
        {
            var asset = assets[x.MediaAssetId];
            if (x.Sha256 != asset.Sha256 || x.SizeBytes != asset.SizeBytes || x.ContentType != asset.ContentType) throw Denied();
            return new AttachmentInput(x.Id, x.MediaAssetId, MetadataHash(new { x.Id, x.OrganizationId, x.ConversationId, x.MessageId,
                x.MediaAssetId, x.FileName, x.ContentType, x.SizeBytes, x.Sha256, x.CreatedAt,
                Asset = new { asset.Id, asset.OrganizationId, asset.CreatingAgentInstallationId, asset.GenAiJobId, asset.WorkstreamId,
                    asset.TeamId, asset.ArtifactId, asset.WorkItemId, asset.BuildId, ProvenanceHash = Hash(asset.ProvenanceJson),
                    asset.FileName, asset.ContentType, asset.SizeBytes, asset.Sha256, asset.StorageKey, asset.CreatedAt } }));
        }).ToArray();
        var renderedAttachments = attachments.Select(x => new PreparedChatAttachment(x.Id, x.MessageId, x.FileName,
            x.ContentType, x.SizeBytes, x.Sha256)).ToArray();
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["messageId"] = binding.MessageId.ToString("D"), ["mentionsJson"] = JsonSerializer.Serialize(renderedMentions, Json),
            ["senderOrganizationUserId"] = sender.Id.ToString("D"), ["senderDisplayName"] = sender.DisplayName,
            ["senderEmployeeType"] = sender.EmployeeType.ToString(), ["senderRole"] = sender.Role?.Name ?? "",
            ["currentUserMessage"] = message.Content,
            ["senderIsReportingAncestor"] = await IsReportingAncestorAsync(binding, token) ? "true" : "false"
        };
        AgentWorkContext? workContext = null; string? workstreamHash = null;
        if (conversation.WorkstreamId is { } workstreamId)
        {
            var stream = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workstreamId && x.OrganizationId == binding.OrganizationId, token)
                ?? throw Denied();
            workContext = new(binding.OrganizationId, workstreamId, conversation.TeamId, null, null, null, null,
                binding.TurnId, binding.MessageId, stream.ProfileKey);
            workstreamHash = MetadataHash(new { stream.Id, stream.OrganizationId, stream.ProfileKey, stream.ProfileVersion,
                stream.ProfileDefinitionDigest, stream.Revision, stream.AccountableManagerOrganizationUserId, stream.Status, conversation.TeamId });
        }
        var metadata = new PreparedChatMetadata(renderedSender, renderedMentions, renderedAttachments, context, workContext);
        var evidence = new AuxiliaryEvidence(1, sender.Id, PersonHash(sender), mentionInputs, attachmentInputs,
            ContextHash(context), MetadataHash(renderedAttachments), workContext is null ? null : MetadataHash(workContext), workstreamHash);
        if (!ValidAuxiliary(binding, evidence) || System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(metadata, Json)) > 131072) throw Denied();
        return new(metadata, evidence);
    }

    private async Task<bool> IsReportingAncestorAsync(Binding binding, CancellationToken token)
    {
        if (binding.EmployeeId == binding.HumanId) return false;
        var seen = new HashSet<Guid>(); var current = binding.EmployeeId;
        for (var depth = 0; depth < 32 && seen.Add(current); depth++)
        {
            var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current &&
                x.OrganizationId == binding.OrganizationId && x.IsActive && x.ArchivedAt == null, token);
            if (employee is null) return false;
            if (current == binding.HumanId) return true;
            if (employee.ReportsToOrganizationUserId is not { } manager) return false;
            current = manager;
        }
        return false;
    }

    private static bool ValidAuxiliary(Binding binding, AuxiliaryEvidence? auxiliary) => auxiliary is { Version: 1, Mentions.Length: <= 100, Attachments.Length: <= 32 } &&
        auxiliary.SenderId == binding.HumanId && Digest(auxiliary.SenderHash) && Digest(auxiliary.ContextHash) && Digest(auxiliary.AttachmentsHash) &&
        (auxiliary.WorkContextHash is null ? auxiliary.WorkstreamSourceHash is null : Digest(auxiliary.WorkContextHash) && Digest(auxiliary.WorkstreamSourceHash)) &&
        auxiliary.Mentions.All(x => x is not null && x.Id != Guid.Empty && x.PersonId != Guid.Empty && Digest(x.Hash)) &&
        auxiliary.Attachments.All(x => x is not null && x.Id != Guid.Empty && x.AssetId != Guid.Empty && Digest(x.Hash)) &&
        auxiliary.Mentions.Select(x => x.Id).Distinct().Count() == auxiliary.Mentions.Length &&
        auxiliary.Attachments.Select(x => x.Id).Distinct().Count() == auxiliary.Attachments.Length;

    private static void ValidateMetadataPayload(Binding binding, AuxiliaryEvidence auxiliary, JsonElement payload)
    {
        if (!payload.TryGetProperty("userId", out var sender) || sender.GetString() != binding.HumanId.ToString("D") ||
            !payload.TryGetProperty("conversationId", out var conversation) || conversation.GetString() != binding.ConversationId.ToString("D") ||
            !payload.TryGetProperty("turnId", out var turn) || turn.GetGuid() != binding.TurnId ||
            !payload.TryGetProperty("attempt", out var attempt) || attempt.GetInt32() != binding.Attempt ||
            !payload.TryGetProperty("messageId", out var message) || message.GetGuid() != binding.MessageId ||
            !payload.TryGetProperty("context", out var context) ||
            ContextHash(context.Deserialize<Dictionary<string, string>>(ErasureJson) ?? throw Denied()) != auxiliary.ContextHash ||
            !payload.TryGetProperty("attachments", out var attachments) ||
            MetadataHash(attachments.Deserialize<PreparedChatAttachment[]>(ErasureJson) ?? throw Denied()) != auxiliary.AttachmentsHash ||
            !payload.TryGetProperty("workContext", out var work) ||
            (work.ValueKind == JsonValueKind.Null ? null : MetadataHash(work.Deserialize<AgentWorkContext>(ErasureJson) ?? throw Denied())) != auxiliary.WorkContextHash)
            throw Denied();
    }
}
