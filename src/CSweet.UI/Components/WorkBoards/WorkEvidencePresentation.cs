using System.Globalization;
using System.Text.Json;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.WorkBoards;

public static class WorkEvidencePresentation
{
    public static IReadOnlyList<WorkEvidenceEntry> Entries(IEnumerable<Wire.WorkExecutionEvidence> evidence)
    {
        var entries = new List<WorkEvidenceEntry>();
        foreach (var item in evidence.Where(x => x.Kind is "artifact-revision" or "commit" or "target-commit"))
        {
            if (item.Kind == "artifact-revision")
            {
                var reference = ParseRevision(item.Value);
                entries.Add(new(ReadableName(item.Name), reference?.ArtifactId, reference?.RevisionId, null));
            }
            else
            {
                // Commit evidence has no repository URL; never treat its arbitrary value as a link.
                entries.Add(new(item.Kind == "target-commit" ? "Target commit" : "Commit", null, null, item.Value));
            }
        }
        return entries.DistinctBy(x => x.ArtifactId.HasValue
            ? $"{x.ArtifactId}:{x.RevisionId}" : $"{x.Label}:{x.Commit}").ToList();
    }

    public static string ReadableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Document";
        var name = value.Trim();
        if (!name.Contains(' ') && name.Contains('.'))
        {
            var parts = name.Split('.');
            name = parts.Length > 1 && parts[^1].StartsWith('v') && int.TryParse(parts[^1].AsSpan(1), out _)
                ? parts[^2] : parts[^1];
        }
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('-', ' ').Replace('_', ' '));
    }

    private static (Guid ArtifactId, Guid RevisionId)? ParseRevision(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            Guid? artifact = null, revision = null;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !property.Value.TryGetGuid(out var id) || id == Guid.Empty) continue;
                if (property.Name.Equals("artifactId", StringComparison.OrdinalIgnoreCase)) artifact = id;
                if (property.Name.Equals("revisionId", StringComparison.OrdinalIgnoreCase)) revision = id;
            }
            return artifact.HasValue && revision.HasValue ? (artifact.Value, revision.Value) : null;
        }
        catch (JsonException) { return null; }
    }
}

public sealed record WorkEvidenceEntry(string Label, Guid? ArtifactId, Guid? RevisionId, string? Commit)
{
    public string? Href(Guid organizationId) => ArtifactId.HasValue && RevisionId.HasValue
        ? $"/organizations/{organizationId:D}/documents?artifact={ArtifactId:D}&revision={RevisionId:D}" : null;
}
