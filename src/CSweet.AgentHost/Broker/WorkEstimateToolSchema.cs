using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

// The wire record owns the envelope. These constraints protect mechanical integrity;
// estimate sufficiency, uncertainty and capacity remain the accountable author's judgments.
internal static class WorkEstimateToolSchema
{
    internal static JsonElement Input { get; } = Create();

    private static JsonElement Create()
    {
        var schema = JsonNode.Parse(ContractToolSchema.Create(typeof(EstimateWorkItemRequest), true).GetRawText())!;
        var fields = schema["properties"]!;
        fields["estimatePoints"]!["minimum"] = 0;
        fields["estimatePoints"]!["maximum"] = 999999.99m;
        fields["expectedItemRevision"]!["minimum"] = 1;
        fields["idempotencyKey"]!["minLength"] = 1;
        fields["idempotencyKey"]!["maxLength"] = 160;
        var provenance = fields["provenance"]!;
        provenance["additionalProperties"] = false;
        var evidence = provenance["properties"]!;
        // Direct owner assessments use revision zero and no coordination session.
        evidence["coordinationTurnRevision"]!["minimum"] = 0;
        evidence["sourceDigest"]!["minLength"] = 1;
        evidence["sourceDigest"]!["maxLength"] = 128;
        evidence["confidence"]!["minimum"] = 0;
        evidence["confidence"]!["maximum"] = 1;
        evidence["planningRevision"]!["minimum"] = 1;
        return JsonSerializer.SerializeToElement(schema);
    }
}
