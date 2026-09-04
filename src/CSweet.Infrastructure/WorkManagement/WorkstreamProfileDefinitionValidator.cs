using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed record ValidatedWorkstreamProfileDefinition(
    string Key,
    int Version,
    string DisplayName,
    string MetadataSchemaJson,
    string LifecyclePolicyKey,
    string DefaultBoardProfileKey,
    string? AuthorityPolicyKey,
    string DefinitionJson,
    string Digest);

public static class WorkstreamProfileDefinitionValidator
{
    public const int MaximumDefinitionBytes = 256 * 1024;

    public static ValidatedWorkstreamProfileDefinition Validate(
        PluginWorkstreamProfileContribution contribution,
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumDefinitionBytes)
            throw new ArgumentException("Workstream profile definition must be between 1 byte and 256 KB.");
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workstream profile definition must be a JSON object.");
        var key = RequiredString(root, "key", 200);
        var version = root.TryGetProperty("version", out var versionElement) && versionElement.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        if (!string.Equals(key, contribution.Key, StringComparison.Ordinal) || version != contribution.Version || version < 1)
            throw new ArgumentException("Workstream profile definition key and version must match the manifest contribution.");
        var displayName = RequiredString(root, "displayName", 256);
        var lifecyclePolicyKey = RequiredString(root, "lifecyclePolicyKey", 200);
        var defaultBoardProfileKey = RequiredString(root, "defaultBoardProfileKey", 200);
        var authorityPolicyKey = OptionalString(root, "authorityPolicyKey", 200);
        if (!root.TryGetProperty("metadataSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workstream profile definition requires an object metadataSchema.");
        ValidateSchema(schema, 0);
        if (!root.TryGetProperty("lifecycle", out var lifecycle) || lifecycle.ValueKind != JsonValueKind.Object ||
            !lifecycle.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array ||
            stages.GetArrayLength() is 0 or > 64)
            throw new ArgumentException("Workstream profile definition requires one to 64 lifecycle stages.");
        var stageKeys = stages.EnumerateArray().Select(x => RequiredString(x, "key", 200)).ToArray();
        if (stageKeys.Distinct(StringComparer.Ordinal).Count() != stageKeys.Length)
            throw new ArgumentException("Workstream lifecycle stage keys must be unique.");
        if (lifecycle.TryGetProperty("transitions", out var transitions))
        {
            if (transitions.ValueKind != JsonValueKind.Array || transitions.GetArrayLength() > 256)
                throw new ArgumentException("Workstream lifecycle transitions must be an array of at most 256 entries.");
            var edges = transitions.EnumerateArray().Select(transition =>
            {
                if (transition.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Every Workstream lifecycle transition must be an object.");
                return (From: RequiredString(transition, "from", 200), To: RequiredString(transition, "to", 200));
            }).ToArray();
            if (edges.Any(edge => !stageKeys.Contains(edge.From, StringComparer.Ordinal) ||
                                  !stageKeys.Contains(edge.To, StringComparer.Ordinal)))
                throw new ArgumentException("Workstream lifecycle transitions must reference declared stages.");
            if (edges.Any(edge => edge.From == edge.To) || edges.Distinct().Count() != edges.Length)
                throw new ArgumentException("Workstream lifecycle transitions must be unique and may not self-transition.");
        }
        if (root.TryGetProperty("milestones", out var milestones) &&
            (milestones.ValueKind != JsonValueKind.Array || milestones.GetArrayLength() > 64))
            throw new ArgumentException("Workstream profile milestones must be an array of at most 64 entries.");
        if (root.TryGetProperty("staffing", out var staffing)) ValidateStaffing(staffing);
        ValidateExecutionTemplate(root);
        ValidateAssignmentPolicy(root);
        if (root.TryGetProperty("workItemTypes", out var workItemTypes))
            _ = WorkstreamProfileWorkTypeCatalog.Read(JsonSerializer.Serialize(root), defaultBoardProfileKey);

        var canonical = JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(key, version, displayName, JsonSerializer.Serialize(schema), lifecyclePolicyKey,
            defaultBoardProfileKey, authorityPolicyKey, canonical, digest);
    }

    private static void ValidateExecutionTemplate(JsonElement root)
    {
        var hasBoard = root.TryGetProperty("boardWorkflow", out var boardElement);
        var hasOrchestration = root.TryGetProperty("orchestration", out var orchestrationElement);
        if (!hasBoard && !hasOrchestration) return;
        if (!hasBoard || !hasOrchestration)
            throw new ArgumentException("Profile execution configuration requires both boardWorkflow and orchestration.");
        var board = boardElement.Deserialize<Wire.WorkBoardWorkflowTemplate>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("The boardWorkflow definition is invalid.");
        var orchestration = orchestrationElement.Deserialize<Wire.WorkOrchestrationProfileTemplate>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("The orchestration definition is invalid.");
        if (board.Columns.Count is < 2 or > 32)
            throw new ArgumentException("A profile board workflow requires between two and 32 columns.");
        if (board.Columns.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != board.Columns.Count ||
            board.Columns.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != board.Columns.Count)
            throw new ArgumentException("Profile board column keys and names must be unique.");
        foreach (var column in board.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Key) || string.IsNullOrWhiteSpace(column.Name))
                throw new ArgumentException("Every profile board column requires a key and name.");
            if (!Enum.TryParse<WorkBoardColumnCategory>(column.Category, true, out _) ||
                !Enum.TryParse<WorkBoardWipPolicy>(column.WipPolicy, true, out _))
                throw new ArgumentException($"Profile board column '{column.Key}' has an invalid category or WIP policy.");
            if (!string.Equals(column.WipPolicy, WorkBoardWipPolicy.Disabled.ToString(), StringComparison.OrdinalIgnoreCase) &&
                column.WipLimit is null or <= 0)
                throw new ArgumentException($"Profile board column '{column.Key}' requires a positive WIP limit.");
        }
        var columnKeys = board.Columns.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (orchestration.Stages.Count is < 1 or > 64 ||
            orchestration.Stages.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != orchestration.Stages.Count)
            throw new ArgumentException("Profile orchestration requires one to 64 uniquely keyed stages.");
        var stageKeys = orchestration.Stages.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (!stageKeys.Contains(orchestration.InitialStageKey))
            throw new ArgumentException("Profile orchestration initial stage is not declared.");
        if (orchestration.Stages.Any(x => x.ColumnKey is not null && !columnKeys.Contains(x.ColumnKey)))
            throw new ArgumentException("Profile orchestration references an unknown board column key.");
        if (orchestration.Transitions.Count > 256 || orchestration.Transitions.Any(x =>
                !stageKeys.Contains(x.FromStageKey) || !stageKeys.Contains(x.ToStageKey) ||
                x.FromStageKey == x.ToStageKey))
            throw new ArgumentException("Profile orchestration transitions are invalid.");
    }

    private static void ValidateAssignmentPolicy(JsonElement root)
    {
        if (!root.TryGetProperty("assignmentPolicy", out var value)) return;
        var policy = value.Deserialize<Wire.WorkAssignmentPolicyTemplate>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("The profile assignmentPolicy is invalid.");
        if (!Wire.WorkSkillMatchModes.All.Contains(policy.SkillMatchMode))
            throw new ArgumentException("The profile assignmentPolicy has an invalid skillMatchMode.");
        if (policy.Roles.Count is < 1 or > 64 ||
            policy.Roles.Select(x => x.RoleKey).Distinct(StringComparer.Ordinal).Count() != policy.Roles.Count)
            throw new ArgumentException("The profile assignmentPolicy requires one to 64 unique role policies.");
        if (policy.PlanningQuorums.Count > 32 ||
            policy.PlanningQuorums.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != policy.PlanningQuorums.Count)
            throw new ArgumentException("The profile assignmentPolicy planning quorums must be unique.");
        foreach (var role in policy.Roles)
        {
            ValidateCanonicalKey(role.RoleKey, "assignment role");
            ValidateCanonicalKeys(role.EligibleWorkItemTypeKeys, 100, "eligible work item types", dotted: true);
            ValidateCanonicalKeys(role.DefaultRelevantSpecializationKeys, 32, "default specializations");
            ValidateCapabilityKeys(role.RequiredCapabilityKeys, 32);
        }
        var roleKeys = policy.Roles.Select(x => x.RoleKey).ToHashSet(StringComparer.Ordinal);
        foreach (var quorum in policy.PlanningQuorums)
        {
            ValidateCanonicalKey(quorum.Key, "planning quorum");
            ValidateCanonicalKeys(quorum.LifecycleStageKeys, 64, "quorum lifecycle stages");
            ValidateCanonicalKeys(quorum.RequiredRoleKeys, 64, "quorum roles");
            if (quorum.RequiredRoleKeys.Any(x => !roleKeys.Contains(x)))
                throw new ArgumentException("Every planning quorum role must have an assignment role policy.");
        }
        if (root.TryGetProperty("workItemTypes", out var workTypes) && workTypes.ValueKind == JsonValueKind.Array)
        {
            var knownTypes = workTypes.EnumerateArray().Select(x => RequiredString(x, "key", 200))
                .ToHashSet(StringComparer.Ordinal);
            if (policy.Roles.SelectMany(x => x.EligibleWorkItemTypeKeys).Any(x => !knownTypes.Contains(x)))
                throw new ArgumentException("Assignment role policies may reference only declared work item types.");
        }
    }

    private static void ValidateCanonicalKeys(
        IReadOnlyList<string> values, int maximum, string label, bool dotted = false)
    {
        if (values.Count > maximum || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException($"Profile {label} must be unique and contain at most {maximum} entries.");
        foreach (var value in values)
        {
            if (dotted)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
                    value.Any(c => !(char.IsLower(c) || char.IsDigit(c) || c is '-' or '.')))
                    throw new ArgumentException($"Profile {label} contains an invalid key.");
            }
            else ValidateCanonicalKey(value, label);
        }
    }

    private static void ValidateCanonicalKey(string value, string label)
    {
        if (!CSweet.Agent.SDK.RoleTaxonomy.IsCanonicalKey(value))
            throw new ArgumentException($"Profile {label} contains an invalid canonical key.");
    }

    private static void ValidateCapabilityKeys(IReadOnlyList<string> values, int maximum)
    {
        if (values.Count > maximum || values.Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200))
            throw new ArgumentException("Profile required capability keys are invalid.");
    }

    public static void ValidateProfileData(JsonElement schema, JsonElement data)
    {
        var errors = new List<string>();
        ValidateValue(schema, data, "$", errors, 0);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors.Take(10)));
    }

    private static void ValidateStaffing(JsonElement staffing)
    {
        if (staffing.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workstream profile staffing must be an object.");
        ValidateRoleKeyArray(staffing, "requiredRoleKeys", 64);
        ValidateRoleKeyArray(staffing, "conditionalRoleKeys", 64);
        if (!staffing.TryGetProperty("conditionalRoles", out var rules)) return;
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > 64)
            throw new ArgumentException("Conditional staffing rules must be an array of at most 64 entries.");
        var roleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Every conditional staffing rule must be an object.");
            var roleKey = RequiredString(rule, "roleKey", 160);
            if (!roleKeys.Add(roleKey))
                throw new ArgumentException("Conditional staffing role keys must be unique.");
            _ = RequiredString(rule, "blockingDecisionTypeKey", 200);
            if (!rule.TryGetProperty("predicate", out var predicate) || predicate.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Every conditional staffing rule requires a predicate object.");
            var jsonPath = RequiredString(predicate, "jsonPath", 256);
            var @operator = RequiredString(predicate, "operator", 32);
            if (!predicate.TryGetProperty("value", out var expected))
                throw new ArgumentException("Every conditional staffing predicate requires a value.");
            BoundedJsonPredicateEvaluator.Validate(jsonPath, @operator, expected);
        }
    }

    private static void ValidateRoleKeyArray(JsonElement staffing, string propertyName, int maximumCount)
    {
        if (!staffing.TryGetProperty(propertyName, out var roles)) return;
        if (roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() > maximumCount)
            throw new ArgumentException($"Staffing {propertyName} must be an array of at most {maximumCount} role keys.");
        var keys = roles.EnumerateArray().Select(role =>
        {
            if (role.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(role.GetString()) || role.GetString()!.Length > 160)
                throw new ArgumentException($"Staffing {propertyName} contains an invalid role key.");
            return role.GetString()!;
        }).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ArgumentException($"Staffing {propertyName} role keys must be unique.");
    }

    private static void ValidateSchema(JsonElement schema, int depth)
    {
        if (depth > 8) throw new ArgumentException("Metadata schema nesting cannot exceed eight levels.");
        var type = RequiredString(schema, "type", 32);
        if (type is not ("object" or "array" or "string" or "number" or "integer" or "boolean"))
            throw new ArgumentException($"Unsupported metadata schema type '{type}'.");
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (type != "object" || properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Count() > 128)
                throw new ArgumentException("Metadata schema properties are invalid.");
            foreach (var property in properties.EnumerateObject()) ValidateSchema(property.Value, depth + 1);
        }
        if (schema.TryGetProperty("items", out var items)) ValidateSchema(items, depth + 1);
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, string path, List<string> errors, int depth)
    {
        if (depth > 8) { errors.Add($"{path} exceeds maximum nesting."); return; }
        var type = schema.GetProperty("type").GetString();
        var valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
        if (!valid) { errors.Add($"{path} must be {type}."); return; }
        if (type == "object")
        {
            var required = schema.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var name in required)
                if (!value.TryGetProperty(name, out _)) errors.Add($"{path}.{name} is required.");
            var properties = schema.TryGetProperty("properties", out var propertySchemas) ? propertySchemas : default;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                    ValidateValue(propertySchema, property.Value, $"{path}.{property.Name}", errors, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    errors.Add($"{path}.{property.Name} is not allowed.");
            }
        }
        if (type == "array" && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateValue(itemSchema, item, $"{path}[{index++}]", errors, depth + 1);
        }
        if (type == "string" && schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(x => x.GetString() == value.GetString())) errors.Add($"{path} is not an allowed value.");
    }

    private static string RequiredString(JsonElement value, string name, int maximumLength)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) || property.GetString()!.Length > maximumLength)
            throw new ArgumentException($"Workstream profile property '{name}' is required and must not exceed {maximumLength} characters.");
        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string name, int maximumLength)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String || property.GetString()!.Length > maximumLength)
            throw new ArgumentException($"Workstream profile property '{name}' must not exceed {maximumLength} characters.");
        return property.GetString();
    }
}
