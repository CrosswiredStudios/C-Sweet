using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class ComputeMcpTools
{
    private const string ReadSchema = """
        {"type":"object","additionalProperties":false,"minProperties":1,"maxProperties":2,"properties":{"environmentId":{"type":"string","format":"uuid"},"operationId":{"type":"string","format":"uuid"},"artifactId":{"type":"string","format":"uuid"},"offset":{"type":"integer","minimum":0,"maximum":4194304},"defaults":{"type":"boolean","enum":[true]},"workstreamId":{"type":"string","format":"uuid","description":"Optional project scope, only valid with defaults:true."}}}
        """;
    private const string ListSchema = """
        {"type":"object","additionalProperties":false,"required":["workstreamId"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"afterId":{"type":"string","format":"uuid"},"limit":{"type":"integer","minimum":1,"maximum":100}}}
        """;
    private const string LifecycleSchema = """
        {"type":"object","additionalProperties":false,"required":["environmentId","expectedGeneration","idempotencyKey"],"properties":{"environmentId":{"type":"string","format":"uuid"},"expectedGeneration":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":128}}}
        """;
    private const string ProvisionSchema = """
        {"type":"object","additionalProperties":false,"required":["workstreamId","desiredEnvironmentKey","idempotencyKey","specification"],"properties":{
          "workstreamId":{"type":"string","format":"uuid"},"desiredEnvironmentKey":{"type":"string","minLength":1,"maxLength":128},"idempotencyKey":{"type":"string","minLength":1,"maxLength":128},
          "specification":{"type":"object","additionalProperties":false,"required":["operatingSystem","architecture","templateId","resources","lifetimeSeconds"],"properties":{
            "operatingSystem":{"type":"string","minLength":1,"maxLength":64},"architecture":{"type":"string","minLength":1,"maxLength":64},"templateId":{"type":"string","minLength":1,"maxLength":64},
            "resources":{"type":"object","additionalProperties":false,"required":["cpuCount","memoryMiB","diskMiB"],"properties":{"cpuCount":{"type":"integer","minimum":1},"memoryMiB":{"type":"integer","minimum":1},"diskMiB":{"type":"integer","minimum":1},"gpuCount":{"type":"integer","minimum":0}}},
            "lifetimeSeconds":{"type":"integer","minimum":0,"description":"Zero retains compute until explicitly released; positive seconds request a timed lease. Subject to grants."},"persistence":{"type":"string","enum":["ephemeral","persistent"]},
            "network":{"type":"object","additionalProperties":false,"properties":{"mode":{"type":"string","enum":["none","private","outboundOnly","inbound"]},"allowOutbound":{"type":"boolean"},"publicEndpoint":{"type":"boolean"},"publishedPorts":{"type":"array","maxItems":64,"items":{"type":"integer","minimum":1,"maximum":65535}}}}
          }}
        }}
        """;

    private const string WorkloadSchema = """
        {"type":"object","additionalProperties":false,"required":["environmentId","expectedGeneration","idempotencyKey","workload"],"properties":{
          "environmentId":{"type":"string","format":"uuid"},"expectedGeneration":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":128},
          "workload":{"type":"object","additionalProperties":false,"minProperties":1,"maxProperties":2,"properties":{
            "evidenceArchive":{"type":"object","additionalProperties":false,"required":["sourceCommitSha","buildSha256","sha256","length"],"properties":{"sourceCommitSha":{"type":"string","pattern":"^[a-f0-9]{40}([a-f0-9]{24})?$"},"buildSha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},"sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},"length":{"type":"integer","minimum":1,"maximum":4194304},"scriptSha256":{"type":"string","pattern":"^[a-f0-9]{64}$"}}},
            "browserTest":{"type":"object","additionalProperties":false,"required":["requestId","sourceCommitSha","buildSha256","viewportWidths"],"properties":{
              "requestId":{"type":"string","format":"uuid"},"sourceCommitSha":{"type":"string","pattern":"^[a-f0-9]{40}([a-f0-9]{24})?$"},"buildSha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},
              "viewportWidths":{"type":"array","minItems":1,"maxItems":4,"uniqueItems":true,"items":{"type":"integer","minimum":240,"maximum":3840}},"requireWebGl":{"type":"boolean","description":"Require an initialized WebGL game canvas; true when omitted."}
            }},
            "nodeDependencies":{"type":"object","additionalProperties":false,"required":["lockfileSha256","packages"],"properties":{
              "lockfileSha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},"packages":{"type":"array","maxItems":256,"items":{"type":"object","additionalProperties":false,"required":["url","integrity"],"properties":{"url":{"type":"string","maxLength":1024},"integrity":{"type":"string","maxLength":95}}}}
            }},
            "publishPort":{"type":"integer","minimum":1024,"maximum":65535},
            "command":{"type":"object","additionalProperties":false,"required":["requestId","executable","workingDirectory","arguments","timeoutSeconds","maximumOutputBytes"],"properties":{
              "requestId":{"type":"string","format":"uuid"},"executable":{"type":"string","maxLength":1024},"workingDirectory":{"type":"string","maxLength":1024},
              "arguments":{"type":"array","maxItems":64,"items":{"type":"string","maxLength":32768}},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":30},"maximumOutputBytes":{"type":"integer","minimum":1,"maximum":8192}
            }}
          }}
        }}
        """;

    public static IReadOnlyList<McpToolDescriptor> All { get; } =
    [
        Tool(InfrastructureActions.Execute, "execute_compute_command", "Queue one bounded command, browser test, or evidenceArchive transfer inside your ready VM. evidenceArchive exports one hash-pinned archive from the build-derived evidence directory (optional checks script hash), up to 4 MiB, under one durable operation ID. Read its artifact using compute.read; no repeated command callbacks are needed. Use workload.command with absolute guest paths and literal arguments; 30 second timeout, 8192 output bytes. Alternatively workload.browserTest runs the pinned offline Playwright/Chromium workload on browser-testing-v1 templates: stage build.zip and an evidence directory at /var/lib/csweet-compute/work/browser-<buildSha256>/, then supply exact sourceCommitSha, buildSha256, requestId, viewportWidths and requireWebGl. Its result identifies the tested bytes, viewport checks and retained screenshots/trace archive. Optional workload.nodeDependencies on ordinary commands supplies a lockfile SHA-256 and SHA-512-pinned public npm tarballs; managed restore requires separate current network.outbound.v1 authority. Read the operationId for output. Lost outcomes must not be blindly retried.", WorkloadSchema),
        Tool(InfrastructureActions.PublishPort, "publish_compute_port", "Publish a running guest HTTP app using workload.publishPort. Requires independent inbound and port grants. Returns an operationId; read its result for a health-checked URL on the provider machine's loopback interface and expiry. This MVP link is usable on that machine only. No NIC, outbound access, public internet URL or DNS is implied.", WorkloadSchema),
        Tool(InfrastructureActions.Provision, "request_compute_environment", "Request isolated compute under current scoped grants. Reuse stable desired-environment and idempotency keys. Provisioning is asynchronous; this response does not mean the machine is ready. Network and persistence require separate grants.", ProvisionSchema),
        Tool(InfrastructureActions.Read, "read_compute_environment", "Read your environment status with environmentId, or command/publication status and result with operationId. Alternatively send defaults:true to read platform-selected workspace and template readiness without technical configuration; optionally include workstreamId for an authorized project. Supply exactly one selector. Re-read after a compute changed event; never resubmit an uncertain command with a new key.", ReadSchema, true),
        Tool(InfrastructureActions.List, "list_compute_environments", "List a bounded page of your own environments in an authorized workstream.", ListSchema, true),
        Tool(InfrastructureActions.Start, "start_compute_environment", "Request start of your environment using its current generation and a stable idempotency key.", LifecycleSchema),
        Tool(InfrastructureActions.Stop, "stop_compute_environment", "Request stop of your environment using its current generation and a stable idempotency key.", LifecycleSchema),
        Tool(InfrastructureActions.Restart, "restart_compute_environment", "Request restart of your environment using its current generation and a stable idempotency key.", LifecycleSchema),
        Tool(InfrastructureActions.Destroy, "destroy_compute_environment", "Request destruction of your environment under an explicit destroy grant. Completion requires confirmed physical teardown.", LifecycleSchema)
    ];

    private static McpToolDescriptor Tool(string capability, string name, string description, string schema, bool read = false) =>
        new(capability, name, description, JsonSerializer.Deserialize<JsonElement>(schema),
            JsonSerializer.SerializeToElement(new { type = "object" }),
            read ? McpToolExecutionPolicy.ReadOnly : McpToolExecutionPolicy.AdvisoryWrite,
            RiskClass: read ? "read-only" : capability == InfrastructureActions.Destroy ? "destructive" : "infrastructure-write",
            ScopeResolver: "organization-installation-workstream", MaximumInputBytes: capability == InfrastructureActions.Execute ? 131072 : 32768, QuotaClass: "compute",
            ApprovalBehavior: "scoped-grant-required", OwningService: "compute");
}
