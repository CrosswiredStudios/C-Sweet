using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    private static readonly JsonSerializerOptions ErasureJson = new(Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true };
    internal sealed record ErasureRead(IReadOnlyList<MemoryErasureTarget> References,
        IReadOnlyList<MemoryPartition> Partitions, Binding? Binding, string? PayloadHash, PromptEvidence? Prompt);

    // Historical inspection deliberately does not require evidence still to be recallable:
    // changed/deleted evidence may remain inside a work payload or runtime context.
    internal static ErasureRead InspectErasureRead(string json, bool queued)
    {
        try
        {
            if (json.Length > (queued ? 131072 : 262144)) throw new JsonException();
            using (var document = JsonDocument.Parse(json)) RequireUniqueFields(document.RootElement);
            Root[] roots; Record[] records; Package[] packages; MemoryPartition[] partitions; Binding? binding = null; string? payloadHash = null;
            PromptEvidence? prompt = null;
            if (queued)
            {
                var receipt = JsonSerializer.Deserialize<Receipt>(json, ErasureJson) ?? throw new JsonException();
                binding = receipt.Binding; payloadHash = receipt.PayloadHash;
                if (receipt.Version is not (1 or 2) || binding is null || binding.Attempt < 0 ||
                    new[] { binding.TurnId, binding.OrganizationId, binding.ConversationId, binding.MessageId, binding.EmployeeId,
                        binding.HumanId, binding.InstallationId }.Any(x => x == Guid.Empty) || !Digest(binding.AuthorityHash) || !Digest(payloadHash) ||
                    receipt.Roots is null || receipt.Roots.Length > 8 ||
                    (receipt.Roots.Length == 0 ? receipt.ContextHash is not null || receipt.Records is not { Length: 0 } : !Digest(receipt.ContextHash)))
                    throw new JsonException();
                roots = receipt.Roots; records = receipt.Records; packages = [];
                if (receipt.Version == 2)
                {
                    if (!ValidPrompt(binding, receipt.Prompt, historical: true)) throw new JsonException();
                    prompt = receipt.Prompt;
                }
                else if (receipt.Prompt is not null) throw new JsonException();
                partitions = roots.Select(x => x.Partition).ToArray();
            }
            else
            {
                var receipt = JsonSerializer.Deserialize<ReadReceipt>(json, ErasureJson) ?? throw new JsonException();
                if (receipt.Version != 1 || receipt.Roots is null || receipt.Roots.Length > 512 ||
                    receipt.Packages is null || receipt.Packages.Length > 1 || receipt.Partitions is null || receipt.Partitions.Length is < 1 or > 32 ||
                    receipt.Roots.Length + receipt.Packages.Length == 0) throw new JsonException();
                roots = receipt.Roots; records = receipt.Records; packages = receipt.Packages; partitions = receipt.Partitions;
            }
            if (records is null || records.Length > 512 || partitions.Any(x => x is null) ||
                records.Any(x => x is null || x.Partition is null || x.Id == Guid.Empty || !Enum.IsDefined(x.Kind) || x.Revision <= 0 || !Digest(x.Hash)) ||
                roots.Any(x => x is null || x.Partition is null || x.Id == Guid.Empty || !Enum.IsDefined(x.Kind) || !Digest(x.ContentHash) ||
                    x.Sources is null || x.Sources.Length > 512 || x.Sources.Any(id => id == Guid.Empty) || x.Format is not ("candidate" or "record" or "transfer")) ||
                packages.Any(x => x is null || x.Id == Guid.Empty || !Digest(x.Hash))) throw new JsonException();
            var references = new HashSet<MemoryErasureTarget>();
            foreach (var record in records) references.Add(new((MemoryErasureKind)record.Kind, record.Id, record.Partition));
            foreach (var root in roots)
            {
                references.Add(new((MemoryErasureKind)root.Kind, root.Id, root.Partition));
                foreach (var source in root.Sources) references.Add(new(MemoryErasureKind.Episode, source, root.Partition));
            }
            foreach (var package in packages)
                foreach (var partition in partitions) references.Add(new(MemoryErasureKind.Transfer, package.Id, partition));
            if (references.Count > 4096) throw new JsonException();
            var audiences = partitions.Concat(references.Select(x => x.Partition)).Distinct().ToArray();
            if (audiences.Length > 64) throw new JsonException();
            return new(references.ToArray(), audiences, binding, payloadHash, prompt);
        }
        catch (Exception error) when (error is JsonException or NullReferenceException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException("memory_erasure_work_lineage_review_required", error); }
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RequireUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException(); RequireUniqueFields(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) RequireUniqueFields(element);
    }
}
