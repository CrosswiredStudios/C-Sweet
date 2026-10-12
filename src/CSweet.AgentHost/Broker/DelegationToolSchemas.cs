using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class DelegationToolSchemas
{
    public static readonly IReadOnlyList<McpToolDescriptor> Tools = DelegationCapabilities.All.Select(capability =>
        new McpToolDescriptor(capability, capability.Replace('.', '_'),
            "Durable delegation or canonical work tracking. Authority and current source access are rechecked by the platform.",
            Input(capability), null, capability is DelegationCapabilities.Read or DelegationCapabilities.ReadTracking or DelegationCapabilities.Pending
                ? McpToolExecutionPolicy.ReadOnly : McpToolExecutionPolicy.AdvisoryWrite)).ToArray();

    public static JsonElement Input(string capability)
    {
        object uuid = new { type = "string", format = "uuid" };
        object nullableUuid = new { type = new[] { "string", "null" }, format = "uuid" };
        object revision = new { type = "integer", minimum = 1 };
        object key = new { type = "string", minLength = 1, maxLength = 160 };
        object text = new { type = "string", minLength = 1, maxLength = 4000 };
        var p = new Dictionary<string, object>();
        var required = new List<string>();
        void Field(string name, object schema, bool mandatory = true) { p.Add(name, schema); if (mandatory) required.Add(name); }
        switch (capability)
        {
            case DelegationCapabilities.Create:
                Field("recipientOrganizationUserId", uuid); Field("instruction", text); Field("intendedOutcome", text);
                Field("sourceConversationId", uuid); Field("sourceMessageId", uuid); Field("idempotencyKey", key);
                Field("interestedParties", new { type = new[] { "array", "null" }, maxItems = 32, items = uuid }, false);
                Field("parentDelegationId", nullableUuid, false);
                Field("workContext", new { type = new[] { "object", "null" }, properties = new { workstreamId = nullableUuid, teamId = nullableUuid, workItemId = nullableUuid }, additionalProperties = false }, false);
                break;
            case DelegationCapabilities.Read:
                Field("delegationId", nullableUuid, false); Field("executionMessageId", nullableUuid, false); break;
            case DelegationCapabilities.Respond:
                Field("delegationId", uuid); Field("expectedRevision", revision);
                Field("disposition", new { type = "string", @enum = new[] { "Accepted", "Clarification", "Declined", "Progress", "Blocked", "Completed", "Failed" } });
                Field("summary", text); Field("idempotencyKey", key); Field("workItemId", nullableUuid, false); break;
            case DelegationCapabilities.Publish:
                Field("delegationId", uuid); Field("updateId", uuid);
                Field("summary", new { type = "string", minLength = 1, maxLength = 1500 }); Field("idempotencyKey", key); break;
            case DelegationCapabilities.Cancel:
                Field("delegationId", uuid); Field("expectedRevision", revision); Field("idempotencyKey", key); break;
            case DelegationCapabilities.Follow:
            case DelegationCapabilities.Unfollow:
                Field("workItemId", uuid); Field("idempotencyKey", key); break;
            case DelegationCapabilities.ReadTracking:
                Field("workItemId", uuid); break;
            case DelegationCapabilities.Progress:
                Field("workItemId", uuid); Field("expectedRevision", revision);
                Field("kind", new { type = "string", @enum = new[] { "Progress", "Milestone", "Blocked", "Decision", "Completed", "Failed" } });
                Field("summary", text); Field("idempotencyKey", key); Field("delegationId", nullableUuid, false); break;
            case DelegationCapabilities.Pending:
                Field("limit", new { type = "integer", minimum = 1, maximum = 50 }, false); break;
            case DelegationCapabilities.Processed:
                Field("workItemId", uuid); Field("updateId", uuid); break;
        }
        return JsonSerializer.SerializeToElement(new { type = "object", properties = p, required, additionalProperties = false });
    }
}
