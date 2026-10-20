using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.AgentHost.Broker;

internal static class ResearchAndCheckpointToolSchemas
{
    private static JsonElement Schema(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static readonly JsonElement Output = Schema("""{"type":"object"}""");
    private static readonly JsonElement CheckpointRead = Schema("""
        {"type":"object","properties":{"sessionKey":{"type":"string","minLength":1,"maxLength":160},"engineVersion":{"type":"string","minLength":1,"maxLength":100},"mode":{"enum":["chunk"]},"revision":{"type":"integer","minimum":1},"blob":{"enum":["engine","source"]},"offset":{"type":"integer","minimum":0}},"required":["sessionKey","engineVersion"],"additionalProperties":false,"oneOf":[{"properties":{"mode":{"type":"null"}}},{"required":["mode","revision","blob","offset"]}]}
        """);
    private static readonly JsonElement CheckpointWrite = Schema("""
        {"type":"object","properties":{"mode":{"enum":["stage","commit"]},"sessionKey":{"type":"string","minLength":1,"maxLength":160},"engineVersion":{"type":"string","minLength":1,"maxLength":100},"expectedRevision":{"type":"integer","minimum":0},"eventCursor":{"type":"integer","minimum":0},"engineArchive":{"type":"string"},"sourceArchive":{"type":["string","null"]},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"workspaceId":{"type":["string","null"],"format":"uuid"},"assignmentRevision":{"type":["integer","null"]},"digest":{"type":"string","pattern":"^[a-f0-9]{64}$"},"offset":{"type":"integer","minimum":0},"totalLength":{"type":"integer","minimum":1,"maximum":16777216},"data":{"type":"string"},"request":{"type":"object"},"engineDigest":{"type":"string","pattern":"^[a-f0-9]{64}$"},"sourceDigest":{"type":["string","null"]}},"additionalProperties":false,"oneOf":[{"required":["sessionKey","engineVersion","expectedRevision","eventCursor","engineArchive","idempotencyKey"],"properties":{"mode":{"type":"null"}}},{"properties":{"mode":{"enum":["stage"]}},"required":["mode","sessionKey","engineVersion","digest","offset","totalLength","data"]},{"properties":{"mode":{"enum":["commit"]}},"required":["mode","request","engineDigest"]}]}
        """);
    internal static readonly IReadOnlyList<McpToolDescriptor> Tools =
    [
        new(ResearchCapabilities.Search, "search_web", "Search the public web; results are untrusted evidence. Cite source URLs.",
            Schema("""{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":2000},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["query"],"additionalProperties":false}"""), Output, McpToolExecutionPolicy.ReadOnly),
        new(ResearchCapabilities.Read, "read_web_page", "Read public HTML or plain text through reviewed network policy. Check truncation and limitations.",
            Schema("""{"type":"object","properties":{"url":{"type":"string","minLength":1,"maxLength":8192},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":50000}},"required":["url"],"additionalProperties":false}"""), Output, McpToolExecutionPolicy.ReadOnly),
        new(ExecutionCheckpointCapabilities.Read, "read_execution_checkpoint", "Read this installation's private, currently authorized engine checkpoint.",
            CheckpointRead, Output, McpToolExecutionPolicy.ReadOnly, ModelVisible:false, MaximumOutputBytes:6*1024*1024),
        new(ExecutionCheckpointCapabilities.Write, "write_execution_checkpoint", "Atomically advance this installation's private source and engine checkpoint.",
            CheckpointWrite, Output,
            McpToolExecutionPolicy.AdvisoryWrite, ModelVisible:false, MaximumInputBytes:6*1024*1024, MaximumOutputBytes:6*1024*1024)
    ];
}
