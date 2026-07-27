using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public enum McpToolExecutionPolicy
{
    ReadOnly,
    AdvisoryWrite,
    ApprovalCreating,
    PlatformOnly
}

public enum McpToolAvailability
{
    GrantRequired,
    PlatformOnly
}

public sealed record McpToolDescriptor(
    string Capability,
    string Name,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    McpToolExecutionPolicy ExecutionPolicy,
    McpToolAvailability Availability = McpToolAvailability.GrantRequired,
    bool ModelVisible = true,
    Guid? ProviderInstallationId = null,
    int ExecutionTimeoutSeconds = 30,
    string RiskClass = "standard",
    string ScopeResolver = "organization-and-installation",
    int MaximumInputBytes = 64 * 1024,
    int MaximumOutputBytes = 1024 * 1024,
    string QuotaClass = "standard",
    string ApprovalBehavior = "none",
    string OwningService = "platform");

public sealed class McpToolCatalog(IEnumerable<IPlatformCapabilityHandler> handlers)
{
    private static readonly JsonElement EmptyInput = Schema("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """);
    private static readonly JsonElement ObjectOutput = Schema("""
        { "type": "object" }
        """);

    private static readonly IReadOnlyList<McpToolDescriptor> Tools =
    [
        Read(PlatformCapabilities.BusinessProfileRead, "read_business_profile",
            "Read the authoritative business profile for this organization."),
        Write(PlatformCapabilities.BusinessProfileUpdateExplicit, "update_explicit_business_profile",
            "Save low-risk facts explicitly stated by the owner, with conversation and message provenance."),
        Approval(PlatformCapabilities.BusinessProfileProposeUpdate, "propose_business_profile_update",
            "Propose inferred or sensitive business-profile changes for owner approval."),
        Read(PlatformCapabilities.OrganizationSnapshotRead, "read_organization_snapshot",
            "Read current staff, roles, reporting lines, objectives, workstreams, workers, and operating signals."),
        Read(PlatformCapabilities.BusinessPatternSearch, "search_business_patterns",
            "Find stage-appropriate operating patterns from broker-approved sources."),
        Approval(PlatformCapabilities.WorkstreamPlanPropose, "propose_workstream_plan",
            "Propose a managed workstream with one accountable manager."),
        Read(PlatformCapabilities.WorkforceSearch, "search_workforce",
            "Search current staff and connected human workforce providers. Installable agent listings require the separate agent-catalog grant."),
        Read(AgentCatalogCapabilities.Search, "get_available_agents",
            "Search organization-installed, local-directory, first-party, and marketplace agents without importing, installing, hiring, or spending."),
        Approval(PlatformCapabilities.WorkforcePlanPropose, "propose_workforce_plan",
            "Propose a workforce plan without installing, hiring, contacting, or spending."),
        Read(PlatformCapabilities.FinanceProfileRead, "read_finance_profile",
            "Read authoritative financial goals and workforce controls."),
        Approval(PlatformCapabilities.FinanceProfileProposeUpdate, "propose_finance_profile_update",
            "Propose changes to financial goals or controls for owner approval."),
        Write(PlatformCapabilities.BudgetEvaluate, "evaluate_budget",
            "Evaluate a proposed cost against enforceable budgets; reservations remain platform controlled."),
        Approval(PlatformCapabilities.ApprovalPropose, "propose_approval",
            "Create a durable, separately gated action proposal."),
        Read(PlatformCapabilities.ManagementCycleRead, "read_management_cycle",
            "Read management cadence, executive briefing schedule, and quiet hours."),
        Write(CommunicationHubCapabilities.AskUser, "ask_user",
            "Ask the user one structured multiple-choice question with two to four mutually exclusive options. Put the recommended option first. The UI automatically adds Something else with a free-text response."),
        Read(HiringCapabilities.ListRecommendations, "list_hiring_recommendations",
            "Read this agent installation's role backlog in priority order."),
        Write(HiringCapabilities.UpsertRecommendation, "upsert_hiring_recommendation",
            "Create or update a prioritized role in this agent installation's hiring backlog. Candidate references may be omitted until sourcing begins."),
        Approval(HiringCapabilities.StageWorkflow, "stage_hiring_workflow",
            "Stage a combined install-and-hire proposal for explicit organization-owner approval. This does not install or hire directly.")
    ];

    static McpToolCatalog()
    {
        var duplicateNames = Tools.GroupBy(x => x.Name, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        var duplicateCapabilities = Tools.GroupBy(x => x.Capability, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        if (duplicateNames.Length > 0 || duplicateCapabilities.Length > 0)
            throw new InvalidOperationException(
                $"The capability registry contains duplicates. Tools: {string.Join(", ", duplicateNames)}; capabilities: {string.Join(", ", duplicateCapabilities)}.");
        foreach (var tool in Tools)
        {
            RequireObjectSchema(tool.Capability, "input", tool.InputSchema);
            JsonSchemaValidator.ValidateSchema(tool.InputSchema);
            if (tool.OutputSchema is { } output)
            {
                RequireObjectSchema(tool.Capability, "output", output);
                JsonSchemaValidator.ValidateSchema(output);
            }
        }
    }

    public IReadOnlyList<McpToolDescriptor> List(IReadOnlySet<string> grantedCapabilities) =>
        Tools.Where(tool => tool.Availability != McpToolAvailability.PlatformOnly &&
                             grantedCapabilities.Contains(tool.Capability))
            .Concat(grantedCapabilities
                .Where(capability => Tools.All(x => x.Capability != capability) &&
                                     handlers.Any(x => x.CanHandle(capability)))
                .Select(capability => new McpToolDescriptor(
                    capability,
                    ToToolName(capability),
                    $"Invoke the granted C-Sweet capability {capability}.",
                    Schema("""{"type":"object","additionalProperties":true}"""),
                    ObjectOutput,
                    McpToolExecutionPolicy.PlatformOnly,
                    McpToolAvailability.GrantRequired,
                    ModelVisible: false,
                    OwningService: "platform")))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();

    public McpToolDescriptor? Find(string name, IReadOnlySet<string> grantedCapabilities) =>
        List(grantedCapabilities).SingleOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    public async Task<IReadOnlyList<McpToolDescriptor>> ListAsync(
        AgentSession session,
        CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var tools = List(session.Grant.RequiredCapabilities).ToList();
        var requesterId = Guid.Parse(session.InstallationId);
        var bindings = await db.AgentCapabilityBindings.AsNoTracking()
            .Where(x => x.RequesterInstallationId == requesterId &&
                        x.OrganizationId == session.BusinessId &&
                        x.GrantRevision == session.Grant.Revision &&
                        x.RevokedAt == null &&
                        x.ProviderInstallation != null &&
                        x.ProviderInstallation.IsEnabled &&
                        x.ProviderInstallation.BusinessId == session.BusinessId &&
                        x.ProviderInstallation.RevisionStatus == PluginRevisionStatus.Active)
            .Include(x => x.ProviderInstallation!)
                .ThenInclude(x => x.PackageVersion)
            .ToListAsync(cancellationToken);
        foreach (var binding in bindings)
        {
            if (!session.Grant.RequiredCapabilities.Contains(binding.Capability))
                continue;
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                binding.ProviderInstallation!.PackageVersion!.ManifestJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var declaration = manifest?.Provides.SingleOrDefault(
                x => string.Equals(x.Name, binding.Capability, StringComparison.Ordinal));
            if (declaration is null ||
                declaration.InputSchema.ValueKind != JsonValueKind.Object ||
                declaration.OutputSchema.ValueKind != JsonValueKind.Object)
                continue;
            JsonSchemaValidator.ValidateSchema(declaration.InputSchema);
            JsonSchemaValidator.ValidateSchema(declaration.OutputSchema);
            tools.Add(new McpToolDescriptor(
                declaration.Name,
                ToToolName(declaration.Name),
                declaration.Description,
                declaration.InputSchema,
                declaration.OutputSchema,
                McpToolExecutionPolicy.AdvisoryWrite,
                ProviderInstallationId: binding.ProviderInstallationId,
                ExecutionTimeoutSeconds: declaration.ExecutionTimeoutSeconds,
                RiskClass: declaration.RiskClass,
                ApprovalBehavior: "policy-dependent",
                OwningService: $"provider:{manifest!.Id}"));
        }
        return tools
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Single())
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<McpToolDescriptor?> FindAsync(
        string name,
        AgentSession session,
        CSweetDbContext db,
        CancellationToken cancellationToken) =>
        (await ListAsync(session, db, cancellationToken))
        .SingleOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    private static McpToolDescriptor Read(string capability, string name, string description) =>
        new(capability, name, description, InputFor(capability), ObjectOutput, McpToolExecutionPolicy.ReadOnly,
            RiskClass: "read-only", OwningService: OwnerFor(capability));

    private static McpToolDescriptor Write(string capability, string name, string description) =>
        new(capability, name, description, InputFor(capability), ObjectOutput, McpToolExecutionPolicy.AdvisoryWrite,
            RiskClass: "reversible-write", ApprovalBehavior: "policy-dependent", OwningService: OwnerFor(capability));

    private static McpToolDescriptor Approval(string capability, string name, string description) =>
        new(capability, name, description, InputFor(capability), ObjectOutput, McpToolExecutionPolicy.ApprovalCreating,
            RiskClass: "approval-required", ApprovalBehavior: "always-create-approval", OwningService: OwnerFor(capability));

    private static string OwnerFor(string capability) =>
        capability.StartsWith("communication.", StringComparison.Ordinal) ? "communication-hub" :
        capability.StartsWith("memory.", StringComparison.Ordinal) ? "memory" :
        capability.Contains("hiring", StringComparison.Ordinal) ? "workforce" :
        capability.StartsWith("platform.agent-catalog", StringComparison.Ordinal) ? "marketplace" :
        "platform";

    private static JsonElement InputFor(string capability) => capability switch
    {
        PlatformCapabilities.BusinessProfileRead or
        PlatformCapabilities.OrganizationSnapshotRead or
        PlatformCapabilities.FinanceProfileRead or
        PlatformCapabilities.ManagementCycleRead or
        HiringCapabilities.ListRecommendations => EmptyInput,
        PlatformCapabilities.BusinessPatternSearch => Schema("""
            {"type":"object","properties":{"businessType":{"type":["string","null"]},"lifecycleStage":{"type":["string","null"]},"jurisdictions":{"type":["array","null"],"items":{"type":"string"}},"maximumResults":{"type":"integer","minimum":1,"maximum":10}},"additionalProperties":false}
            """),
        PlatformCapabilities.WorkforceSearch => Schema("""
            {"type":"object","required":["requiredCapabilities","humanRequired"],"properties":{"requiredCapabilities":{"type":"array","items":{"type":"string"},"minItems":1},"requiredCredentials":{"type":["array","null"],"items":{"type":"string"}},"neededBy":{"type":["string","null"],"format":"date-time"},"maximumBudget":{"type":["number","null"],"minimum":0},"currency":{"type":["string","null"]},"humanRequired":{"type":"boolean"},"workstreamId":{"type":["string","null"]},"maximumResults":{"type":"integer","minimum":1,"maximum":25}},"additionalProperties":false}
            """),
        AgentCatalogCapabilities.Search => Schema("""
            {"type":"object","properties":{"role":{"type":["string","null"],"maxLength":160},"searchString":{"type":["string","null"],"maxLength":500},"requiredCapabilities":{"type":["array","null"],"items":{"type":"string"}},"category":{"type":["string","null"],"maxLength":160},"maximumPrice":{"type":["number","null"],"minimum":0},"currency":{"type":["string","null"],"maxLength":8},"sort":{"type":["string","null"],"enum":["relevance","rating","price-low","name",null]},"limit":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
            """),
        PlatformCapabilities.BusinessProfileUpdateExplicit => Schema("""
            {"type":"object","required":["expectedRevision","conversationId","messageId","userId","changes","idempotencyKey"],"properties":{"expectedRevision":{"type":"integer"},"conversationId":{"type":"string"},"messageId":{"type":"string"},"userId":{"type":"string"},"changes":{"type":"object"},"idempotencyKey":{"type":"string"}},"additionalProperties":false}
            """),
        PlatformCapabilities.BudgetEvaluate => Schema("""
            {"type":"object","required":["scopeType","amount","currency","purpose","reserve","idempotencyKey"],"properties":{"scopeType":{"type":"string"},"scopeId":{"type":["string","null"]},"amount":{"type":"number","minimum":0},"currency":{"type":"string"},"purpose":{"type":"string"},"reserve":{"type":"boolean"},"idempotencyKey":{"type":"string"}},"additionalProperties":false}
            """),
        CommunicationHubCapabilities.AskUser => Schema("""
            {"type":"object","required":["conversationId","chatTurnId","prompt","options","recommendedOptionId","idempotencyKey"],"properties":{"conversationId":{"type":"string","format":"uuid"},"chatTurnId":{"type":"string","format":"uuid"},"prompt":{"type":"string","minLength":1,"maxLength":2048},"options":{"type":"array","minItems":2,"maxItems":4,"items":{"type":"object","required":["id","label"],"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"label":{"type":"string","minLength":1,"maxLength":160},"description":{"type":["string","null"],"maxLength":500}},"additionalProperties":false}},"recommendedOptionId":{"type":"string","minLength":1,"maxLength":80},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        HiringCapabilities.UpsertRecommendation => Schema("""
            {"type":"object","required":["title","objective","priority","candidateReferences","idempotencyKey"],"properties":{"title":{"type":"string","minLength":1,"maxLength":256},"objective":{"type":"string","minLength":1,"maxLength":2048},"priority":{"type":"integer","minimum":1,"maximum":100,"description":"1 is the highest priority"},"workstreamId":{"type":["string","null"],"format":"uuid"},"candidateReferences":{"type":"array","maxItems":3,"items":{"type":"string"}},"recommendedCandidateReference":{"type":["string","null"]},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        HiringCapabilities.StageWorkflow => Schema("""
            {"type":"object","required":["recommendationId","candidateReference","roleTitle","idempotencyKey"],"properties":{"recommendationId":{"type":"string","format":"uuid"},"candidateReference":{"type":"string"},"roleTitle":{"type":"string","minLength":1,"maxLength":160},"reportsToOrganizationUserId":{"type":["string","null"],"format":"uuid"},"requiredGrants":{"type":["array","null"],"items":{"type":"string"}},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        _ => Schema("""
            {"type":"object","description":"Arguments are validated by the broker capability handler."}
            """)
    };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void RequireObjectSchema(string capability, string direction, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "object")
            throw new InvalidOperationException(
                $"Capability '{capability}' has an invalid {direction} schema. Registry schemas must have an object root.");
    }

    private static string ToToolName(string capability)
    {
        var withoutVersion = capability.EndsWith(".v1", StringComparison.Ordinal)
            ? capability[..^3]
            : capability;
        return string.Concat(withoutVersion.Select(x => char.IsLetterOrDigit(x) ? char.ToLowerInvariant(x) : '_'))
            .Trim('_');
    }
}
