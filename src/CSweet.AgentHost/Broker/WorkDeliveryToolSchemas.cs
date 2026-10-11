using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class WorkDeliveryToolSchemas
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    internal static JsonElement Input(string capability)
    {
        var type = capability switch
        {
            WorkDeliveryCapabilities.Read => typeof(ReadWorkDeliveryPlansRequest),
            WorkDeliveryCapabilities.Configure => typeof(ConfigureWorkDeliveryPlanRequest),
            WorkDeliveryCapabilities.Control => typeof(ControlWorkDeliveryPlanRequest),
            WorkDeliveryCapabilities.Accept => typeof(DecideWorkDeliveryAcceptanceRequest),
            WorkDeliveryCapabilities.Recover => typeof(RecoverWorkDeliveryRequest),
            WorkDeliveryCapabilities.Review => typeof(CompleteWorkDeliveryReviewRequest),
            WorkDeliveryCapabilities.Evidence => typeof(ReadWorkDeliveryEvidenceRequest),
            _ => throw new ArgumentException("Unknown delivery capability.")
        };
        var schema = Json.GetJsonSchemaAsNode(type);
        schema["type"] = "object";
        ExpandLocalReferences(schema, schema.DeepClone());
        Normalize(schema);
        schema["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(schema);
    }
    // The .NET exporter reuses repeated record types with property-path references.
    // Our bounded broker validator accepts root definitions only; inline these acyclic request types.
    private static void ExpandLocalReferences(JsonNode? node, JsonNode root, int depth = 0)
    {
        if (depth > 32) throw new InvalidOperationException("Delivery request schema nesting exceeds the supported limit.");
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference)
            {
                var path = reference.GetValue<string>();
                if (!path.StartsWith("#/", StringComparison.Ordinal)) throw new InvalidOperationException("Only local delivery schema references are supported.");
                JsonNode? target = root;
                foreach (var segment in path[2..].Split('/'))
                    target = target?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
                if (target is not JsonObject definition) throw new InvalidOperationException("Unknown delivery schema reference.");
                obj.Remove("$ref");
                foreach (var property in definition) obj[property.Key] = property.Value?.DeepClone();
            }
            foreach (var property in obj.ToArray()) ExpandLocalReferences(property.Value, root, depth + 1);
        }
        else if (node is JsonArray array)
            foreach (var child in array) ExpandLocalReferences(child, root, depth + 1);
    }
    private static void Normalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("default");
            foreach (var child in obj.ToArray())
            {
                if (child.Key is "properties" or "$defs" && child.Value is JsonObject schemas)
                    foreach (var property in schemas.ToArray())
                    {
                        if (property.Value is JsonValue literal && literal.TryGetValue<bool>(out var allowed) && allowed)
                            schemas[property.Key] = new JsonObject();
                        else Normalize(property.Value);
                    }
                else if (child.Key == "items" && child.Value is JsonValue items && items.TryGetValue<bool>(out var anyItem) && anyItem)
                    obj[child.Key] = new JsonObject();
                else Normalize(child.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) Normalize(child);
    }
}
