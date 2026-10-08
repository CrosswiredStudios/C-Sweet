using CSweet.Application.Core;
using CSweet.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    public async Task<PreparedMemoryRecall> PrepareTurnRecallAsync(Guid turnId, bool includeMemory = true, CancellationToken cancellationToken = default)
    {
        var evidence = new MemoryRecallDispatchEvidence(db);
        var binding = await evidence.ReadBindingAsync(turnId, cancellationToken);
        MemoryRecallDispatchEvidence.Root[] roots = [];
        MemoryRecallDispatchEvidence.Record[] records = [];
        string? context = null;
        if (includeMemory)
        {
            var query = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == binding.MessageId).Select(x => x.Content).SingleAsync(cancellationToken);
            context = await RecallForConversationCoreAsync(binding.ConversationId, query, binding.EmployeeId, binding.HumanId, cancellationToken,
                async selected =>
                {
                    (roots, records) = await evidence.CaptureAsync(selected, cancellationToken);
                    await evidence.RequireAudienceAsync(binding, roots, cancellationToken);
                });
        }
        if (binding != await evidence.ReadBindingAsync(turnId, cancellationToken)) throw new ProviderDispatchDeniedException();
        if (string.IsNullOrWhiteSpace(context)) { context = null; roots = []; records = []; }
        var prompt = await evidence.CapturePromptAsync(binding, context, cancellationToken);
        if (binding != await evidence.ReadBindingAsync(turnId, cancellationToken)) throw new ProviderDispatchDeniedException();
        var receipt = new MemoryRecallDispatchEvidence.Receipt(2, binding,
            context is null ? null : MemoryRecallDispatchEvidence.Hash(context), roots, records) { Prompt = prompt.Evidence };
        return new(context, MemoryRecallDispatchEvidence.Serialize(receipt))
        { ConversationPrompt = prompt.Conversation, CurrentMessageContent = prompt.Current, AgentPrompt = prompt.Agent, Metadata = prompt.Metadata };
    }
}
