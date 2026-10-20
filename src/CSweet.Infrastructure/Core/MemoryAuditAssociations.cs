using System.Text.Json;

namespace CSweet.Infrastructure.Core;

/// <summary>Recognizes only content-free activity links matching the retained primary record.</summary>
internal static class MemoryAuditAssociations
{
    internal static bool Matches(string? metadata, Guid? workItem, Guid? conversation, Guid? perspective)
    {
        if (metadata is null) return true; // Older audit receipts have no activity links.
        try
        {
            using var document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var expected = new Dictionary<string, Guid?>(StringComparer.Ordinal)
            {
                ["activityWorkItemId"] = workItem,
                ["activityConversationId"] = conversation,
                ["activityPerspectiveEmployeeId"] = perspective
            };
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (!expected.Remove(field.Name, out var value)) return false;
                if (value is null)
                {
                    if (field.Value.ValueKind != JsonValueKind.Null) return false;
                }
                else if (field.Value.ValueKind != JsonValueKind.String ||
                    !field.Value.TryGetGuid(out var actual) || actual != value) return false;
            }
            return expected.Count == 0;
        }
        catch (JsonException) { return false; }
    }
}
