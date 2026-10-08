using System.Text.Json;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    private static MemoryPartition[] SharedAudienceClosure(Record[] records)
    {
        var partitions = new List<MemoryPartition>();
        foreach (var record in records)
        {
            if (record.SharedAudienceJson is null) continue;
            if (record.Kind != MemoryRecordKind.Episode || record.SharedAudienceJson.Length > 16384) throw Denied();
            var shared = JsonSerializer.Deserialize<MemoryPartition[]>(record.SharedAudienceJson, Json) ?? throw Denied();
            if (shared.Length > MemorySharedAudiences.MaximumPartitions) throw Denied();
            partitions.AddRange(shared);
        }
        return MemorySharedAudiences.Merge(partitions);
    }

    internal async Task RequireSharedAudienceAsync(Binding binding, Record[] records, CancellationToken token)
    {
        try { await MemorySharedAudienceAuthorization.RequireAsync(db, binding.OrganizationId, binding.EmployeeId, binding.HumanId,
            SharedAudienceClosure(records), token); }
        catch (UnauthorizedAccessException) { throw Denied(); }
    }

    public async Task AuthorizeChatReadSharedAudienceAsync(string json, Guid turnId, Guid organization, Guid employee, CancellationToken token)
    {
        if (json.Length > 262144) throw Denied();
        var receipt = JsonSerializer.Deserialize<ReadReceipt>(json, Json) ?? throw Denied();
        if (receipt.Version != 1 || receipt.Records is not { Length: <= 512 }) throw Denied();
        var binding = await ReadBindingAsync(turnId, token);
        if (binding.OrganizationId != organization || binding.EmployeeId != employee) throw Denied();
        await RequireSharedAudienceAsync(binding, receipt.Records, token);
        if (binding != await ReadBindingAsync(turnId, token)) throw Denied();
    }
}
