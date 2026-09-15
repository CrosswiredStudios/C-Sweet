using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class ComputeMcpTools
{
    private const string ReadSchema = """
        {"type":"object","additionalProperties":false,"minProperties":1,"maxProperties":1,"properties":{"environmentId":{"type":"string","format":"uuid"},"operationId":{"type":"string","format":"uuid"},"defaults":{"type":"boolean","enum":[true]}}}
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
          "workload":{"type":"object","additionalProperties":false,"minProperties":1,"maxProperties":1,"properties":{
            "publishPort":{"type":"integer","minimum":1024,"maximum":65535},
            "command":{"type":"object","additionalProperties":false,"required":["requestId","executable","workingDirectory","arguments","timeoutSeconds","maximumOutputBytes"],"properties":{
              "requestId":{"type":"string","format":"uuid"},"executable":{"type":"string","maxLength":1024},"workingDirectory":{"type":"string","maxLength":1024},
              "arguments":{"type":"array","maxItems":64,"items":{"type":"string","maxLength":24000}},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":30},"maximumOutputBytes":{"type":"integer","minimum":1,"maximum":8192}
            }}
          }}
        }}
        """;

    public static IReadOnlyList<McpToolDescriptor> All { get; } =
    [
        Tool(InfrastructureActions.Execute, "execute_compute_command", "Queue one bounded command inside your ready VM. Use workload.command with absolute guest paths and literal arguments; 30 second timeout, 8192 output bytes. Read the returned operationId for output. Services must be started explicitly inside the guest (for example systemd-run); children of the bounded command are cleaned up. Lost outcomes must not be blindly retried.", WorkloadSchema),
        Tool(InfrastructureActions.PublishPort, "publish_compute_port", "Publish a running guest HTTP app using workload.publishPort. Requires independent inbound and port grants. Returns an operationId; read its result for a health-checked URL on the provider machine's loopback interface and expiry. This MVP link is usable on that machine only. No NIC, outbound access, public internet URL or DNS is implied.", WorkloadSchema),
        Tool(InfrastructureActions.Provision, "request_compute_environment", "Request isolated compute under current scoped grants. Reuse stable desired-environment and idempotency keys. Provisioning is asynchronous; this response does not mean the machine is ready. Network and persistence require separate grants.", ProvisionSchema),
        Tool(InfrastructureActions.Read, "read_compute_environment", "Read your environment status with environmentId, or command/publication status and result with operationId. Alternatively send defaults:true to read platform-selected workspace and template readiness without technical configuration. Supply exactly one selector. Re-read after a compute changed event; never resubmit an uncertain command with a new key.", ReadSchema, true),
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
            ScopeResolver: "organization-installation-workstream", MaximumInputBytes: 32768, QuotaClass: "compute",
            ApprovalBehavior: "scoped-grant-required", OwningService: "compute");
}
