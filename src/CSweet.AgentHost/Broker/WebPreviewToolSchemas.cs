using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.WebHost.Contracts;
namespace CSweet.AgentHost.Broker;

internal static class WebPreviewToolSchemas
{
    public static JsonElement Input(string capability)
    {
        if (capability == WebPreviewCapabilities.List) return JsonSerializer.Deserialize<JsonElement>("""{"type":"object","required":["projectId"],"properties":{"projectId":{"type":"string","format":"uuid"},"afterId":{"type":["string","null"],"format":"uuid"},"limit":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}""");
        if (capability == WebPreviewCapabilities.Renew) return JsonSerializer.Deserialize<JsonElement>("""{"type":"object","required":["previewId","totalLifetimeSeconds","idempotencyKey"],"properties":{"previewId":{"type":"string","format":"uuid"},"totalLifetimeSeconds":{"type":"integer","minimum":300},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}""");
        if (capability == WebPreviewCapabilities.Test) return JsonSerializer.Deserialize<JsonElement>("""{"type":"object","required":["previewId","idempotencyKey","checks"],"properties":{"previewId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"checks":{"type":"array","minItems":1,"maxItems":10,"items":{"type":"object","required":["path"],"properties":{"path":{"type":"string","maxLength":8192},"selector":{"type":["string","null"],"maxLength":256},"expectedText":{"type":["string","null"],"maxLength":2048}},"additionalProperties":false}}},"additionalProperties":false}""");
        if (capability == WebPreviewCapabilities.Build)
        {
            var build = JsonNode.Parse("""{"type":"object","required":["preview","toolchainDefinitionId","toolchainProviderInstallationId","recipeKey","targetKey","configuration"],"properties":{"preview":{},"toolchainDefinitionId":{"type":"string","format":"uuid"},"toolchainProviderInstallationId":{"type":"string","format":"uuid"},"recipeKey":{"type":"string","maxLength":200},"targetKey":{"type":"string","maxLength":200},"configuration":{"type":"object"},"maximumAttempts":{"type":"integer","minimum":1,"maximum":5}},"additionalProperties":false}""")!;
            build["properties"]!["preview"] = JsonNode.Parse(Input(WebPreviewCapabilities.Preflight).GetRawText());
            return JsonSerializer.SerializeToElement(build);
        }
        if (capability is WebPreviewCapabilities.Read or WebPreviewCapabilities.Stop or WebPreviewCapabilities.Diagnostics)
        {
            var identity = JsonNode.Parse("""{"type":"object","properties":{"previewId":{"type":"string","format":"uuid"}},"required":["previewId"],"additionalProperties":false}""")!;
            if (capability == WebPreviewCapabilities.Diagnostics)
            {
                identity["properties"]!["afterSequence"] = JsonNode.Parse("""{"type":"integer","minimum":0}""");
                identity["properties"]!["limit"] = JsonNode.Parse("""{"type":"integer","minimum":1,"maximum":256}""");
            }
            return JsonSerializer.SerializeToElement(identity);
        }
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
        if (capability != WebPreviewCapabilities.RequestGrant)
            root["properties"]!["buildId"] = JsonNode.Parse("""{"type":"string","format":"uuid"}""");
        if (capability == WebPreviewCapabilities.Start) ((JsonArray)root["required"]!).Add("buildId");
        return JsonSerializer.SerializeToElement(root);
    }
}
