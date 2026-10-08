using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    private static Receipt ReadQueuedReceipt(string json)
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 131072) throw Denied();
            using (var document = JsonDocument.Parse(json)) RequireUniqueFields(document.RootElement);
            var receipt = JsonSerializer.Deserialize<Receipt>(json, ErasureJson) ?? throw Denied();
            if (receipt.Binding is null || receipt.Roots is null || receipt.Records is null) throw Denied();
            return receipt;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { throw Denied(); }
    }
    internal sealed record PromptInput(Guid Id, long Sequence, ConversationRole Role, DateTimeOffset CreatedAt,
        Guid? SenderId, Guid? ChatTurnId, string SourceProvider, string SourceHash, string RenderedHash, int RenderedLength);
    internal sealed record PromptEvidence(int Version, ConversationKind Kind, Guid? ConversationEmployeeId, Guid InitiatorId,
        PromptInput[] Inputs, string ConversationHash, int ConversationLength, string AgentPromptHash)
    {
        public AuxiliaryEvidence? Auxiliary { get; init; }
    }
    internal sealed record FrozenPrompt(string Current, string Conversation, string Agent, PreparedChatMetadata Metadata, PromptEvidence Evidence);

    internal async Task<FrozenPrompt> CapturePromptAsync(Binding binding, string? context, CancellationToken token)
    {
        var conversation = await db.CoreConversations.AsNoTracking().SingleAsync(x => x.Id == binding.ConversationId, token);
        var current = await db.CoreConversationMessages.AsNoTracking().SingleAsync(x => x.Id == binding.MessageId, token);
        var recent = await db.CoreConversationMessages.AsNoTracking().Where(x => x.ConversationId == binding.ConversationId &&
            x.Sequence < current.Sequence && x.SourceProvider != CommunicationMessageTypes.SystemAction &&
            !db.MemorySourceInvalidations.Any(marker => marker.SourceMessageId == x.Id) &&
            !db.MemoryCaptureExclusions.Any(marker => marker.SourceMessageId == x.Id))
            .OrderByDescending(x => x.Sequence).ThenByDescending(x => x.Id).Take(ConversationPromptRenderer.RecentMessageLimit).ToListAsync(token);
        var bounded = ConversationPromptRenderer.Bound(recent.Select(x => new ConversationPromptMessage(x.Sequence,
            x.Role == ConversationRole.User ? "user" : "assistant", x.Content)).ToArray());
        PromptInput Source(ConversationMessage row, string rendered) => new(row.Id, row.Sequence, row.Role, row.CreatedAt,
            row.SenderOrganizationUserId, row.ChatTurnId, row.SourceProvider, Hash(row.Content), Hash(rendered), rendered.Length);
        var sources = bounded.Select(x => Source(recent.Single(r => r.Sequence == x.Sequence), x.Content))
            .Append(Source(current, current.Content)).ToArray();
        var prompt = ConversationPromptRenderer.Render(context, current.Content, bounded);
        var auxiliary = await CaptureMetadataAsync(binding, token);
        var agentPrompt = ConversationPromptRenderer.RenderPrimary(binding.ConversationId, binding.TurnId, binding.MessageId,
            prompt, auxiliary.Metadata.Sender, auxiliary.Metadata.Mentions);
        var evidence = new PromptEvidence(2, conversation.Kind, conversation.AgentOrganizationUserId,
            conversation.InitiatedByOrganizationUserId, sources, Hash(prompt), prompt.Length, Hash(agentPrompt)) { Auxiliary = auxiliary.Evidence };
        await ValidatePromptAsync(binding, evidence, token);
        return new(current.Content, prompt, agentPrompt, auxiliary.Metadata, evidence);
    }

    public static string BindRenderedPrompt(string receiptJson, string conversationPrompt, string agentPrompt)
    {
        var receipt = ReadQueuedReceipt(receiptJson);
        var prompt = receipt.Prompt ?? throw Denied();
        if (receipt.Version != 2 || !ValidPrompt(receipt.Binding, prompt) || prompt.ConversationLength != conversationPrompt.Length || prompt.ConversationHash != Hash(conversationPrompt) ||
            !agentPrompt.EndsWith(conversationPrompt, StringComparison.Ordinal) || Hash(agentPrompt) != prompt.AgentPromptHash) throw Denied();
        return receiptJson;
    }

    internal static void ValidatePromptPayload(string receiptJson, JsonElement payload)
    {
        try
        {
            var receipt = ReadQueuedReceipt(receiptJson);
            RequireUniqueFields(payload);
            if (receipt.Version != 2 || !ValidPrompt(receipt.Binding, receipt.Prompt) ||
                payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.String || Hash(message.GetString()!) != receipt.Prompt!.AgentPromptHash) throw Denied();
            if (payload.EnumerateObject().Any(x => x.Name is not ("providerProfileId" or "conversationId" or "userId" or "message" or "context" or
                "turnId" or "attempt" or "messageId" or "attachments" or "workContext"))) throw Denied();
            ValidateMetadataPayload(receipt.Binding, receipt.Prompt!.Auxiliary!, payload);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { throw Denied(); }
    }

    private async Task ValidatePromptAsync(Binding binding, PromptEvidence evidence, CancellationToken token)
    {
        if (!ValidPrompt(binding, evidence)) throw Denied();
        var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.ConversationId, token);
        if (conversation is null || conversation.OrganizationId != binding.OrganizationId || conversation.Kind != evidence.Kind ||
            conversation.AgentOrganizationUserId != evidence.ConversationEmployeeId || conversation.InitiatedByOrganizationUserId != evidence.InitiatorId) throw Denied();
        var ids = evidence.Inputs.Select(x => x.Id).ToArray();
        var rows = await db.CoreConversationMessages.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        if (rows.Count != ids.Length || await db.MemorySourceInvalidations.AnyAsync(x => ids.Contains(x.SourceMessageId), token) ||
            await db.MemoryCaptureExclusions.AnyAsync(x => ids.Contains(x.SourceMessageId), token)) throw Denied();
        foreach (var input in evidence.Inputs)
        {
            var row = rows[input.Id];
            if (row.ConversationId != binding.ConversationId || row.Sequence != input.Sequence || row.Role != input.Role ||
                row.CreatedAt != input.CreatedAt || row.SenderOrganizationUserId != input.SenderId || row.ChatTurnId != input.ChatTurnId ||
                row.SourceProvider != input.SourceProvider || Hash(row.Content) != input.SourceHash || row.Content.Length < input.RenderedLength ||
                Hash(row.Content[..input.RenderedLength]) != input.RenderedHash) throw Denied();
        }
        var metadata = await CaptureMetadataAsync(binding, token);
        if (MetadataHash(metadata.Evidence) != MetadataHash(evidence.Auxiliary)) throw Denied();
    }

    private static bool ValidPrompt(Binding binding, PromptEvidence? evidence, bool historical = false) => evidence is { Inputs.Length: >= 1 and <= 21 } &&
        (evidence.Version == 2 && ValidAuxiliary(binding, evidence.Auxiliary) || historical && evidence.Version == 1 && evidence.Auxiliary is null) &&
        evidence.Inputs.All(x => x is not null) &&
        Enum.IsDefined(evidence.Kind) && evidence.InitiatorId != Guid.Empty && Digest(evidence.ConversationHash) && Digest(evidence.AgentPromptHash) &&
        evidence.ConversationLength is > 0 and <= 1_048_576 && evidence.Inputs[^1].Id == binding.MessageId &&
        evidence.Inputs[^1].Role == ConversationRole.User && evidence.Inputs.Select(x => x.Id).Distinct().Count() == evidence.Inputs.Length &&
        evidence.Inputs.All(x => x.Id != Guid.Empty && Enum.IsDefined(x.Role) && x.RenderedLength >= 0 && x.RenderedLength <= 1_048_576 &&
            x.SourceProvider is { Length: > 0 and <= 200 } && Digest(x.SourceHash) && Digest(x.RenderedHash)) &&
        evidence.Inputs.Take(evidence.Inputs.Length - 1).All(x => x.Sequence < evidence.Inputs[^1].Sequence && x.RenderedLength <= ConversationPromptRenderer.MessageCharacterLimit) &&
        evidence.Inputs.Take(evidence.Inputs.Length - 1).Sum(x => x.RenderedLength) <= ConversationPromptRenderer.RecentCharacterBudget;
}
