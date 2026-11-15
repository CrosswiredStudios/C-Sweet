using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class WorkIssueToolSchemas
{
    public static JsonElement Input(string capability)
    {
        const string uuid = "{\"type\":\"string\",\"format\":\"uuid\"}";
        const string text = "{\"type\":\"string\",\"minLength\":1}";
        const string key = "{\"type\":\"string\",\"minLength\":1,\"maxLength\":160}";
        const string source = "{\"type\":\"object\",\"required\":[\"documentId\",\"revisionId\",\"digest\"],\"properties\":{\"documentId\":{\"type\":\"string\",\"format\":\"uuid\"},\"revisionId\":{\"type\":\"string\",\"format\":\"uuid\"},\"digest\":{\"type\":\"string\",\"minLength\":1}},\"additionalProperties\":false}";
        var properties = new Dictionary<string,object> { ["boardId"] = Read(uuid) };
        string[] required;
        if (capability == WorkIssueCapabilities.Read)
        {
            required = ["boardId"];
            properties["includeHistory"] = Read("{\"type\":\"boolean\"}");
            properties["page"] = Read("{\"type\":\"integer\",\"minimum\":1}");
            properties["pageSize"] = Read("{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}");
        }
        else
        {
            properties["source"] = Read(source); properties["idempotencyKey"] = Read(key);
            if (capability == WorkIssueCapabilities.Commission)
            {
                required = ["boardId", "source", "idempotencyKey", "targetEmployeeId", "issueIds", "question"];
                properties["targetEmployeeId"] = Read(uuid); properties["question"] = Read(text);
                properties["issueIds"] = new { type = "array", minItems = 1, uniqueItems = true, items = Read(uuid) };
                properties["supersededSessionIds"] = new { type = "array", uniqueItems = true, items = Read(uuid) };
            }
            else
            {
                required = ["boardId", "source", "idempotencyKey", "issues"];
                properties["issues"] = Read("""
                    {"type":"array","items":{"type":"object","required":["obligationKey","statement","detail","scopeKeys"],"properties":{
                    "obligationKey":{"type":"string","minLength":1,"maxLength":160},"statement":{"type":"string","minLength":1},"detail":{"type":"string","minLength":1},
                    "scopeKeys":{"type":"array","items":{"type":"string","minLength":1}},"issueId":{"type":["string","null"],"format":"uuid"},"expectedRevision":{"type":["integer","null"],"minimum":0},
                    "reopeningEvidence":{"type":["object","null"],"required":["kind","id"],"properties":{"kind":{"type":"string","enum":["Coordination","Decision","Incident","WorkItem"]},"id":{"type":"string","format":"uuid"},"digest":{"type":["string","null"]}},"additionalProperties":false},
                    "status":{"type":"string","enum":["Open","Resolved","Superseded"]},"phase":{"type":"string","enum":["Working","AwaitingEvidence","AwaitingDecision","AwaitingReview"]},
                    "nextAction":{"type":"string","minLength":1},"resolutionEvidence":{"type":["string","null"]},"briefTitle":{"type":["string","null"]},"briefSummary":{"type":["string","null"]},
                    "links":{"type":"array","items":{"type":"object","required":["kind","id"],"properties":{"kind":{"type":"string","enum":["Coordination","Decision","Incident","WorkItem"]},"id":{"type":"string","format":"uuid"},"digest":{"type":["string","null"]}},"additionalProperties":false}}
                    },"additionalProperties":false}}
                    """);
            }
        }
        return JsonSerializer.SerializeToElement(new { type = "object", required, properties, additionalProperties = false });
    }
    private static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
