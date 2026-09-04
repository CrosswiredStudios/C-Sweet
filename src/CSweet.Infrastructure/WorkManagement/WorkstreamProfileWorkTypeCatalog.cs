using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Interprets bounded declarative work types from an immutable Workstream profile.</summary>
public static class WorkstreamProfileWorkTypeCatalog
{
    public static IReadOnlyList<WorkItemTypeDefinition> Read(string definitionJson, string boardProfileKey)
    {
        using var document = JsonDocument.Parse(definitionJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("workItemTypes", out var types) || types.ValueKind != JsonValueKind.Array)
            return [];
        if (types.GetArrayLength() > 100)
            throw new InvalidOperationException("A Workstream profile cannot declare more than 100 work item types.");
        var profileKey = root.GetProperty("key").GetString()!;
        var result = types.EnumerateArray().Select(type =>
        {
            var kind = RequireKind(type);
            var executionMode = type.TryGetProperty("executionMode", out var mode)
                ? mode.GetString()
                : WorkItemExecutionModes.DefaultForKind(kind);
            if (executionMode is null || !WorkItemExecutionModes.All.Contains(executionMode))
                throw new InvalidOperationException("Workstream profile work item executionMode is invalid.");
            return new WorkItemTypeDefinition(
                Required(type, "key", 200),
                Required(type, "displayName", 200),
                kind,
                [boardProfileKey],
                Strings(type, "permittedParentTypeKeys", 100),
                profileKey,
                Strings(type, "requiredApprovalPolicyKeys", 32))
            {
                ExecutionMode = executionMode
            };
        }).ToList();
        if (result.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidOperationException("Workstream profile work item type keys must be unique.");
        return result;
    }

    public static WorkItemTypeDefinition RequireType(
        string definitionJson,
        string boardProfileKey,
        string typeKey,
        string? parentTypeKey)
    {
        var type = Read(definitionJson, boardProfileKey).SingleOrDefault(x => x.Key == typeKey)
            ?? throw new ArgumentException($"Unknown Workstream-profile work item type '{typeKey}'.");
        if (parentTypeKey is null)
        {
            if (type.Kind is not (WorkItemKinds.Initiative or WorkItemKinds.Epic) && type.PermittedParentTypeKeys.Count > 0)
                throw new ArgumentException($"Work item type '{typeKey}' requires a compatible parent.");
        }
        else if (!type.PermittedParentTypeKeys.Contains(parentTypeKey, StringComparer.Ordinal))
            throw new ArgumentException($"Work item type '{typeKey}' cannot be parented by '{parentTypeKey}'.");
        return type;
    }

    private static string RequireKind(JsonElement value)
    {
        var kind = Required(value, "kind", 40);
        return kind is WorkItemKinds.Initiative or WorkItemKinds.Epic or WorkItemKinds.Story or WorkItemKinds.Task or WorkItemKinds.Bug
            ? kind
            : throw new InvalidOperationException($"Unsupported Workstream-profile work item kind '{kind}'.");
    }

    private static string Required(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) || property.GetString()!.Length > maximum)
            throw new InvalidOperationException($"Workstream profile work item type '{name}' is required and must not exceed {maximum} characters.");
        return property.GetString()!;
    }

    private static IReadOnlyList<string> Strings(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property)) return [];
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maximum ||
            property.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()) || x.GetString()!.Length > 200))
            throw new InvalidOperationException($"Workstream profile work item type '{name}' is invalid.");
        return property.EnumerateArray().Select(x => x.GetString()!).Distinct(StringComparer.Ordinal).ToList();
    }
}
