using System.Text;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal sealed partial class MemoryCaptureErasure
{
    private sealed record DispatchReceipt(MemoryExtractionInputReceipt Receipt, AgentMemoryService.ExtractionErasureEvidence Evidence);

    private async Task<IReadOnlyList<DispatchReceipt>> ReadDispatchEvidenceAsync(Guid organization, Guid[]? jobs,
        Guid[]? sources, CancellationToken token)
    {
        var query = db.MemoryExtractionInputReceipts.AsNoTracking();
        query = jobs is not null ? query.Where(x => jobs.Contains(x.JobId)) :
            query.Where(x => x.OrganizationId == organization || sources!.Contains(x.Job!.ConversationMessageId));
        var rows = query.OrderBy(x => x.Id).Select(x => new { Receipt = x,
            Organization = x.Job!.ConversationMessage!.Conversation!.OrganizationId,
            Message = x.Job.ConversationMessageId, Conversation = x.Job.ConversationMessage.ConversationId,
            Generation = x.Job.RetryGeneration }).Take(8193);
        long bytes = 0; var count = 0;
        var result = new List<DispatchReceipt>();
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(token))
        {
            bytes += Encoding.UTF8.GetByteCount(row.Receipt.EvidenceJson);
            if (++count > 8192 || bytes > MaximumBytes) throw Limit();
            if (row.Organization != organization || row.Receipt.OrganizationId != organization) throw new UnauthorizedAccessException();
            var evidence = AgentMemoryService.InspectDispatchInputsForErasure(row.Receipt);
            if (row.Generation < row.Receipt.RetryGeneration || evidence.ConversationId != row.Conversation || evidence.Inputs[0].Id != row.Message)
                throw new InvalidOperationException("memory_erasure_capture_lineage_review_required");
            result.Add(new(row.Receipt, evidence));
        }
        return result.AsReadOnly();
    }
}
