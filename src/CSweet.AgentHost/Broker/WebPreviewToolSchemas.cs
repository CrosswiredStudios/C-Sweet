using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.WebHost.Contracts;
namespace CSweet.AgentHost.Broker;

internal static class WebPreviewToolSchemas
{
    public static JsonElement Input(string capability)
    {
        var resource=JsonNode.Parse("""
            {"type":"object","properties":{"cpuCount":{"type":"integer","minimum":1},"memoryMb":{"type":"integer","minimum":128},
            "diskMb":{"type":"integer","minimum":64},"maximumProcesses":{"type":"integer","minimum":1},
            "maximumLogBytes":{"type":"integer","minimum":1}},
            "required":["cpuCount","memoryMb","diskMb","maximumProcesses","maximumLogBytes"],"additionalProperties":false}
            """)!;
        var root=JsonNode.Parse(capability==WebPreviewCapabilities.RequestGrant ? """
            {"type":"object","properties":{"projectId":{"type":"string","format":"uuid"},
            "providerInstallationId":{"type":"string","format":"uuid"},"repositoryIds":{"type":"array","minItems":1,"maxItems":100,
            "items":{"type":"string","format":"uuid"}},"maximumResources":{},
            "maximumConcurrentPreviews":{"type":"integer","minimum":1},"maximumCpuSeconds":{"type":"integer","minimum":1},
            "maximumLifetimeSeconds":{"type":"integer","minimum":300},"expiresAt":{"type":"string","format":"date-time"},
            "reason":{"type":"string","minLength":1,"maxLength":2000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},
            "required":["projectId","providerInstallationId","repositoryIds","maximumResources","maximumConcurrentPreviews",
            "maximumCpuSeconds","maximumLifetimeSeconds","expiresAt","reason","idempotencyKey"],"additionalProperties":false}
            """ : """
            {"type":"object","properties":{"projectId":{"type":"string","format":"uuid"},"repositoryId":{"type":"string","format":"uuid"},
            "providerInstallationId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},
            "manifest":{"type":"object","properties":{"version":{"type":"integer","enum":[1]},"mode":{"type":"string","enum":["static","containers"]},
            "sourceRevision":{"type":"string","minLength":40,"maxLength":64},"artifactDigest":{"type":["string","null"],"maxLength":71},
            "compose":{"type":["object","null"]},"entrypoint":{"type":["object","null"],"properties":{"service":{"type":"string"},
            "port":{"type":"integer","minimum":1,"maximum":65535},"healthPath":{"type":"string","maxLength":8192}},
            "required":["service","port"],"additionalProperties":false},"resources":{},"lifetimeSeconds":{"type":"integer","minimum":300},
            "connectionIds":{"type":"array","items":{"type":"string"}}},"required":["version","mode","sourceRevision","resources",
            "lifetimeSeconds","connectionIds"],"additionalProperties":false}},
            "required":["projectId","repositoryId","providerInstallationId","manifest","idempotencyKey"],"additionalProperties":false}
            """)!;
        if(capability==WebPreviewCapabilities.RequestGrant) root["properties"]!["maximumResources"]=resource;
        else root["properties"]!["manifest"]!["properties"]!["resources"]=resource;
        return JsonSerializer.SerializeToElement(root);
    }
}
