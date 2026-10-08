using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Domain.Core;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    internal sealed record ExtractionErasureEvidence(MemoryPartition Partition, Guid ConversationId,
        Guid InstallationId, IReadOnlyList<ExtractionErasureInput> Inputs);
    internal sealed record ExtractionErasureInput(Guid Id, ConversationRole Role, string Checksum, DateTimeOffset CreatedAt);

    internal static ExtractionErasureEvidence InspectExtractionForErasure(string json, Guid primary, Guid conversation)
    {
        try
        {
            if (json.Length > 1_048_576) throw new JsonException();
            using (var document = JsonDocument.Parse(json)) CheckFields(document.RootElement);
            var accepted = JsonSerializer.Deserialize<AcceptedMemoryExtraction>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new JsonException();
            var sources = accepted.Sources;
            if (!HasVerifiableEnvelope(accepted) || accepted.Episode is not { } episode || episode.Partition is null ||
                episode.Id != primary || sources is null || sources.ConversationId != conversation || sources.InstallationId == Guid.Empty ||
                sources.Messages[0].Id != primary || sources.Messages[0].Role != ConversationRole.User ||
                sources.Messages.Any(x => x.Id == Guid.Empty || x.Checksum is not { Length: 64 } || !x.Checksum.All(Uri.IsHexDigit)) ||
                sources.Messages.Select(x => x.Id).Distinct().Count() != sources.Messages.Length ||
                sources.Messages.Length == 2 && sources.Messages[1].Role != ConversationRole.Assistant)
                throw new JsonException();
            if (!string.Equals(episode.Checksum, sources.Messages[0].Checksum, StringComparison.OrdinalIgnoreCase) ||
                !ExtractionContentMatches(episode.Content, sources.Messages)) throw new JsonException();
            return new(episode.Partition, conversation, sources.InstallationId,
                sources.Messages.Select(x => new ExtractionErasureInput(x.Id, x.Role, x.Checksum, x.CreatedAt)).ToArray());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new InvalidOperationException("memory_erasure_capture_lineage_review_required", error); }
    }

    private static bool ExtractionContentMatches(string content, SourceFingerprint[] sources)
    {
        bool Matches(string value, int index) => string.Equals(SourceChecksum(value), sources[index].Checksum, StringComparison.OrdinalIgnoreCase);
        if (sources.Length == 1) return Matches(content, 0);
        const string prefix = "<user_turn>\n", separator = "\n</user_turn>\n<assistant_turn>\n", suffix = "\n</assistant_turn>";
        if (!content.StartsWith(prefix, StringComparison.Ordinal) || !content.EndsWith(suffix, StringComparison.Ordinal)) return false;
        // Literal delimiters can occur in user content. Choose only a split matching
        // both recorded hashes, with a finite bound on ambiguous framing work.
        var offset = prefix.Length;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var split = content.IndexOf(separator, offset, StringComparison.Ordinal);
            if (split < 0) return false;
            var assistant = split + separator.Length;
            if (assistant > content.Length - suffix.Length) return false;
            if (Matches(content[prefix.Length..split], 0) && Matches(content[assistant..^suffix.Length], 1)) return true;
            offset = split + 1;
        }
        return false;
    }

    private static void CheckFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                CheckFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) CheckFields(element);
    }
}
