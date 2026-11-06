using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class WorkstreamProposalToolSchema
{
    internal static readonly JsonElement Input = Create();

    private static JsonElement Create()
    {
        var schema = JsonNode.Parse(ContractToolSchema.Create(typeof(WorkstreamPlanProposalV2Request)).GetRawText())!;
        // Types/property names come from the wire contract. Explicit business bounds remain
        // broker policy; adding a contract property can no longer silently omit it here.
        var properties = schema["properties"]!;
        properties["name"]!["minLength"] = 1;
        properties["name"]!["maxLength"] = 240;
        properties["outcome"]!["minLength"] = 1;
        properties["outcome"]!["maxLength"] = 4000;
        properties["profileVersion"]!["minimum"] = 1;
        properties["expectedProjectRevision"]!["minimum"] = 1;
        schema["required"] = JsonSerializer.SerializeToNode(new[] {
            "name", "outcome", "successCriteria", "lifecycleStage", "accountableManagerOrganizationUserId",
            "initialSupervisors", "requiredCapabilities", "rationale", "idempotencyKey", "profileKey",
            "profileVersion", "profileData", "authorityEnvelope", "initialMilestones", "initialEvidence"
        });
        return JsonSerializer.SerializeToElement(schema);
    }
}
