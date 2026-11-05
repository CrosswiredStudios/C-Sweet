using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Agents;

public sealed class HarnessWorkspaceService(CSweetDbContext db, IAgentConfigurationService configurations,
    IAgentInteractiveRuntimeService runtimes, ILlmProviderSecretStore secrets, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] Stages = ["Default", "Planning", "Execution", "Review", "Judgment"];
    public const int MaximumFallbacks = 3;

    public static async Task<HarnessPolicy> ReadPolicyAsync(CSweetDbContext db, Guid installation, CancellationToken token)
    {
        var row = await db.AgentHarnessPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installation, token);
        return row is null ? new() : (JsonSerializer.Deserialize<HarnessPolicy>(row.PolicyJson, Json)
            ?? throw new InvalidOperationException("Invalid saved harness policy.")) with { Revision = row.Revision };
    }

    public async Task<HarnessWorkspace> ReadAsync(Guid installationId, CancellationToken token)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant)
            .SingleAsync(x => x.Id == installationId, token);
        var policy = await ReadPolicyAsync(db, installationId, token);
        var skills = await db.BusinessSkillRevisions.AsNoTracking().Where(x => x.BusinessId == installation.BusinessId)
            .OrderBy(x => x.Name).ThenByDescending(x => x.Version).Take(200).ToListAsync(token);
        var findings = new List<DoctorFinding>();
        var employee = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.AgentInstallationId == installationId)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        var employeeUrl = employee is { } id && Guid.TryParse(installation.BusinessId, out var org)
            ? $"/organizations/{org:D}/employees/{id:D}" : "/settings/agents";
        findings.Add(new("installation", installation.IsEnabled && installation.SetupState == PluginSetupState.Ready ? "Ready" : "Blocked",
            "Installation", $"Setup: {installation.SetupState}; enabled: {installation.IsEnabled}.", employeeUrl));
        findings.Add(new("grant", installation.Grant is null ? "Blocked" : "Ready", "Capability approval",
            installation.Grant is null ? "This installation has no approved capability grant." : $"Grant revision {installation.Grant.GrantRevision} is approved.", employeeUrl));
        var runtime = await runtimes.GetStatusAsync(installationId, token); // Read only; never EnsureReadyAsync.
        findings.Add(new("runtime", runtime.IsReady ? "Ready" : runtime.IsTerminal ? "Blocked" : "Info", "Runtime",
            runtime.Reason is null ? $"Current stage: {runtime.Stage}." : Setup.AuditPayloadSanitizer.RedactText(runtime.Reason), employeeUrl));
        EffectiveAgentConfiguration settings;
        try { settings = await configurations.ResolveInstallationAsync(installationId, token); }
        catch (AgentInstallationException)
        {
            findings.Add(new("configuration", "Blocked", "Agent configuration", "The effective agent configuration could not be resolved. Review the installation configuration and package.", employeeUrl));
            settings = new(installationId, "", new Dictionary<string, JsonElement>(), 0, "");
        }
        var providers = (policy.Routes ?? []).SelectMany(x => (x.Fallbacks ?? []).Select(f => f.ProviderId).Prepend(x.ProviderId)).ToHashSet();
        findings.AddRange(await RouteFindingsAsync(policy, installation.Grant?.RequiredCapabilitiesJson, employeeUrl, token));
        if (settings.Settings.TryGetValue("llmProviderId", out var provider) && provider.ValueKind == JsonValueKind.String && Guid.TryParse(provider.GetString(), out var providerId)) providers.Add(providerId);
        // Every attempt reserves the full output allowance up front, so a smaller ceiling fails every call.
        if (policy.TaskTokenLimit is { } ceiling && settings.Settings.Count > 0)
        {
            int output;
            try { output = Setup.AgentTokenBudget.ResolveOutput(settings.Settings); }
            catch (Exception error) when (error is AgentInstallationException or ArgumentException or InvalidOperationException) { output = 0; }
            if (output > 0 && ceiling <= output)
                findings.Add(new("budget", "Blocked", "Task token ceiling", $"The task ceiling ({ceiling:N0} tokens) does not exceed the per-call output budget ({output:N0}). Each attempt reserves its output budget, so every model call will be refused. Raise the ceiling or lower the output budget.", employeeUrl));
        }
        if (providers.Count == 0) findings.Add(new("provider", "Blocked", "Model provider", "No provider is configured for this installation.", employeeUrl));
        foreach (var providerKey in providers)
        {
            var profile = await db.LlmProviderProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == providerKey, token);
            var missingCredential = false;
            try { missingCredential = profile?.ApiKeySecretName is { Length: > 0 } key && string.IsNullOrWhiteSpace(await secrets.GetAsync(key, token)); }
            catch (Exception error) when (error is not OperationCanceledException) { missingCredential = true; }
            findings.Add(new($"provider:{providerKey:D}", profile is { IsEnabled: true } && !missingCredential ? "Ready" : "Blocked",
                profile?.Name ?? "Missing provider", profile is null ? "The saved provider no longer exists." : !profile.IsEnabled ? "This provider is disabled." : missingCredential ? "The configured credential is missing." : "Provider configuration is present. This check does not make a paid connection test.", "/settings/llm-providers"));
        }
        var pool = installation.ExecutionPoolId is { } poolId ? await db.ExecutionPools.AsNoTracking().SingleOrDefaultAsync(x => x.Id == poolId, token) : null;
        if (pool is not null)
        {
            var cutoff = clock.GetUtcNow().AddMinutes(-2);
            var online = await db.ExecutionNodes.AsNoTracking().AnyAsync(x => x.ExecutionPoolId == pool.Id && x.Status == ExecutionNodeStatus.Ready && x.WorkPauseReason == null && x.RevokedAt == null && x.LastHeartbeatAt > cutoff, token);
            findings.Add(new("office", pool.IsEnabled && online ? "Ready" : "Blocked", "Office compute",
                !pool.IsEnabled ? "The assigned execution pool is disabled." : online ? "The assigned pool has a recent Office heartbeat. Workload placement may impose additional requirements." : "No recent Office heartbeat is available in the assigned pool.", "/settings/offices"));
        }
        else findings.Add(new("office", "Info", "Office compute", "No explicit execution pool is assigned; platform placement rules apply.", "/settings/offices"));
        findings.AddRange(await ComputeFindingsAsync(installation, employeeUrl, token));
        var pending = await db.AgentCapabilityApprovals.AsNoTracking().CountAsync(x => x.AgentInstallationId == installationId && x.Status == "Pending", token);
        findings.Add(new("approvals", pending > 0 ? "Blocked" : "Ready", "Pending operations", $"{pending} operations await a human decision.", employeeUrl));
        var recovery = await db.AgentWorkItems.AsNoTracking().Where(x => x.AgentInstallationId == installationId && x.ResourceRecoveryState != null && (x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased))
            .Select(x => new { x.ResourceRecoveryState, x.ResourceCheckpointDigest }).Take(20).ToListAsync(token);
        findings.Add(new("checkpoints", recovery.Count > 0 ? "Attention" : "Ready", "Recovery checkpoints",
            recovery.Count == 0 ? "No active work is waiting on resource recovery." : $"{recovery.Count} active work items have recovery state; {recovery.Count(x => x.ResourceCheckpointDigest != null)} have a recorded checkpoint.", employeeUrl));
        var recentRuns = await db.AgentRunLogs.AsNoTracking().Where(x => x.AgentInstallationId == installationId).OrderByDescending(x => x.StartedAt).Take(100).Select(x => x.Id).ToArrayAsync(token);
        var judgments = await db.AuditEvents.AsNoTracking().Where(x => x.EventType == "model.judgment.validated" && x.EntityId.HasValue && recentRuns.Contains(x.EntityId.Value)).Select(x => x.Outcome).ToListAsync(token);
        findings.Add(new("judgments", "Info", "Judgment output quality", judgments.Count == 0 ? "No validated judgments are recorded among the most recent 100 model calls." : $"{judgments.Count(x => x == "Valid")} of {judgments.Count} recent judgments matched the allowed schema. This measures format validity, not correctness.", employeeUrl));
        var availableProviders = await db.LlmProviderProfiles.AsNoTracking().Where(x => x.IsEnabled).OrderBy(x => x.Name)
            .Select(x => new HarnessProviderOption(x.Id, x.Name, x.DefaultChatModel)).ToArrayAsync(token);
        return new(policy, skills.Select(Map).ToArray(), findings, clock.GetUtcNow(), availableProviders);
    }

    public async Task<HarnessPolicy> SaveAsync(Guid installation, Guid actor, SaveHarnessPolicy request, CancellationToken token)
    {
        Validate(request.Policy);
        await using var transaction = await GovernanceTransaction.OpenAsync(db, $"harness:{installation:D}", token);
        var target = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleAsync(x => x.Id == installation, token);
        var row = await db.AgentHarnessPolicies.SingleOrDefaultAsync(x => x.Id == installation, token);
        if ((row?.Revision ?? 0) != request.ExpectedRevision) throw new DbUpdateConcurrencyException();
        foreach (var provider in (request.Policy.Routes ?? []).SelectMany(x => (x.Fallbacks ?? []).Select(f => f.ProviderId).Prepend(x.ProviderId)).Distinct())
            if (!await db.LlmProviderProfiles.AnyAsync(x => x.Id == provider && x.IsEnabled, token))
                throw new ArgumentException("Every route and fallback must select an enabled provider.");
        var ids = request.Policy.SkillRevisionIds ?? [];
        var skills = await db.BusinessSkillRevisions.AsNoTracking().Where(x => ids.Contains(x.Id) && x.BusinessId == target.BusinessId && x.Enabled).ToListAsync(token);
        if (skills.Count != ids.Count) throw new ArgumentException("Assign enabled skill revisions owned by this business.");
        var capabilities = JsonSerializer.Deserialize<string[]>(target.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (skills.Any(x => (JsonSerializer.Deserialize<string[]>(x.RequiredCapabilitiesJson) ?? []).Except(capabilities, StringComparer.Ordinal).Any()))
            throw new ArgumentException("A selected skill requires capabilities outside this installation's approved grant.");
        row ??= new() { Id = installation };
        if (db.Entry(row).State == EntityState.Detached) db.AgentHarnessPolicies.Add(row);
        var saved = request.Policy with { Revision = ++row.Revision };
        row.PolicyJson = JsonSerializer.Serialize(saved, Json); row.UpdatedAt = clock.GetUtcNow();
        Audit(installation, actor, "agents.harness.policy.saved", row.Revision.ToString());
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return saved;
    }

    public async Task<HarnessSkillRevision> PublishAsync(Guid installation, Guid actor, PublishHarnessSkill request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || string.IsNullOrWhiteSpace(request.Instructions) || request.Instructions.Length > 32000 || request.RequiredCapabilities is null || request.RequiredCapabilities.Count > 50 || request.RequiredCapabilities.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200))
            throw new ArgumentException("Provide a skill name, instructions (up to 32,000 characters) and bounded capability names.");
        var business = await db.AgentInstallations.Where(x => x.Id == installation).Select(x => x.BusinessId).SingleAsync(token);
        await using var transaction = await GovernanceTransaction.OpenAsync(db, $"skill:{business}:{request.Name.Trim()}", token);
        var version = (await db.BusinessSkillRevisions.Where(x => x.BusinessId == business && x.Name == request.Name.Trim()).MaxAsync(x => (int?)x.Version, token) ?? 0) + 1;
        var required = request.RequiredCapabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { name = request.Name.Trim(), instructions = request.Instructions, required }, Json))).ToLowerInvariant();
        var row = new BusinessSkillRevision { Id = Guid.NewGuid(), BusinessId = business, Name = request.Name.Trim(), Version = version,
            Digest = digest, Instructions = request.Instructions, RequiredCapabilitiesJson = JsonSerializer.Serialize(required), CreatedBy = actor, CreatedAt = clock.GetUtcNow() };
        db.BusinessSkillRevisions.Add(row); Audit(installation, actor, "agents.harness.skill.published", $"{row.Id:D}:{digest}");
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return Map(row);
    }

    public async Task DisableSkillAsync(Guid installation, Guid revision, Guid actor, CancellationToken token)
    {
        var business = await db.AgentInstallations.Where(x => x.Id == installation).Select(x => x.BusinessId).SingleAsync(token);
        var row = await db.BusinessSkillRevisions.SingleOrDefaultAsync(x => x.Id == revision && x.BusinessId == business, token) ?? throw new KeyNotFoundException();
        if (row.CreatedBy != actor)
        {
            var owner = Guid.TryParse(business, out var organization) && await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organization && x.ApplicationUserId == actor && x.IsActive && x.ArchivedAt == null &&
                x.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Owner, token);
            if (!owner) throw new UnauthorizedAccessException("Only the publisher or business owner can disable this shared skill revision.");
        }
        row.Enabled = false; Audit(installation, actor, "agents.harness.skill.disabled", revision.ToString("D"));
        await db.SaveChangesAsync(token);
    }

    public static void Validate(HarnessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.JudgmentOutputTokens is < 64 or > 4096 || policy.TaskTokenLimit is <= 0 || policy.TaskCostLimit is < 0.000001m || policy.TaskCostLimit > 1_000_000 || (policy.Routes?.Count ?? 0) > 32 || (policy.SkillRevisionIds?.Count ?? 0) > 20)
            throw new ArgumentException("Use positive task ceilings, at most thirty-two stage routes and twenty skills.");
        if (policy.TaskCostLimit is { } costCeiling && decimal.Round(costCeiling, 10) != costCeiling) throw new ArgumentException("Cost ceilings support at most ten decimal places.");
        var routes = policy.Routes ?? [];
        if (routes.Any(x => x is null)) throw new ArgumentException("Routes cannot contain null entries.");
        if (routes.Select(x => x.Stage).Distinct(StringComparer.Ordinal).Count() != routes.Count || routes.Any(x => (string.IsNullOrWhiteSpace(x.Stage) || x.Stage.Length > 100) || x.ProviderId == Guid.Empty || string.IsNullOrWhiteSpace(x.Model) || x.Model.Length > 200 || x.InputPricePerMillion is < 0 or > 1000000 || x.OutputPricePerMillion is < 0 or > 1000000))
            throw new ArgumentException("Each stage requires one valid provider/model route and nonnegative prices.");
        foreach (var route in routes)
        {
            var fallbacks = route.Fallbacks ?? [];
            if (fallbacks.Count > MaximumFallbacks || fallbacks.Any(x => x is null || x.ProviderId == Guid.Empty || string.IsNullOrWhiteSpace(x.Model) || x.Model.Length > 200 ||
                    x.InputPricePerMillion is < 0 or > 1000000 || x.OutputPricePerMillion is < 0 or > 1000000))
                throw new ArgumentException($"Each route allows at most {MaximumFallbacks} valid fallback targets with nonnegative prices.");
            // A repeated target would just re-hit the same outage and double-charge the reservation.
            var targets = fallbacks.Select(x => (x.ProviderId, x.Model)).Prepend((route.ProviderId, route.Model)).ToArray();
            if (targets.Distinct().Count() != targets.Length) throw new ArgumentException($"The {route.Stage} route repeats a provider/model target.");
        }
        if (policy.TaskCostLimit.HasValue && (routes.Count == 0 || routes.Any(x => x.InputPricePerMillion is null || x.OutputPricePerMillion is null ||
                (x.Fallbacks ?? []).Any(f => f.InputPricePerMillion is null || f.OutputPricePerMillion is null))))
            throw new ArgumentException("A monetary ceiling requires explicit input and output prices for every route and fallback target.");
        if ((policy.SkillRevisionIds ?? []).Distinct().Count() != (policy.SkillRevisionIds?.Count ?? 0)) throw new ArgumentException("Skill revisions must be distinct.");
    }

    // VM readiness from recorded state only: no provider wake, no VM start, no reservation.
    private async Task<IReadOnlyList<DoctorFinding>> ComputeFindingsAsync(AgentInstallation installation, string employeeUrl, CancellationToken token)
    {
        var granted = JsonSerializer.Deserialize<string[]>(installation.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (!granted.Any(x => x.StartsWith("compute.", StringComparison.Ordinal)) || !Guid.TryParse(installation.BusinessId, out var organization)) return [];
        var placeable = await db.ComputeTemplatePlacements.AsNoTracking().AnyAsync(p => p.OrganizationId == organization && p.Enabled &&
            db.ComputeNodes.Any(n => n.Id == p.NodeId && n.Enabled) && db.ComputeTemplates.Any(t => t.Id == p.TemplateRegistrationId && t.Enabled), token);
        var environments = await db.ComputeEnvironments.AsNoTracking().Where(x => x.OrganizationId == organization && x.InstallationId == installation.Id &&
                x.TeardownConfirmedAt == null)
            .Select(x => new { x.State, x.LastFailureCode }).Take(200).ToListAsync(token);
        var failed = environments.Where(x => x.State == CSweet.Domain.Compute.ComputeLifecycleState.Failed).ToList();
        var ready = environments.Count(x => x.State is CSweet.Domain.Compute.ComputeLifecycleState.Ready or CSweet.Domain.Compute.ComputeLifecycleState.Busy);
        var findings = new List<DoctorFinding>
        {
            new("compute:placement", placeable ? "Ready" : "Blocked", "VM placement", placeable
                ? "An enabled compute node has an enabled guest template. Provisioning may still fail on node capacity or a missing image."
                : "This agent may request VMs, but no enabled compute node has an enabled guest template. Finish compute setup for this business.", "/settings/offices")
        };
        if (environments.Count > 0)
            findings.Add(new("compute:environments", failed.Count > 0 ? "Attention" : "Ready", "Agent VMs",
                $"{ready} ready, {environments.Count} holding resources" +
                (failed.Count > 0 ? $", {failed.Count} failed ({string.Join(", ", failed.Select(x => x.LastFailureCode ?? "unknown").Distinct().Take(3))}). Failed VMs keep their reservation until teardown is confirmed." : "."),
                employeeUrl));
        return findings;
    }

    // Configuration-only checks: no provider call is made and nothing is changed.
    private async Task<IReadOnlyList<DoctorFinding>> RouteFindingsAsync(HarnessPolicy policy, string? grantJson, string employeeUrl, CancellationToken token)
    {
        var findings = new List<DoctorFinding>();
        var granted = JsonSerializer.Deserialize<string[]>(grantJson ?? "[]") ?? [];
        var routes = policy.Routes ?? [];
        if (routes.Count == 0)
            findings.Add(new("routing", "Info", "Model routing", "No stage routes are saved; each request uses the agent's own approved provider and model.", employeeUrl));
        else
        {
            var names = await db.LlmProviderProfiles.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, token);
            string Name(Guid id) => names.TryGetValue(id, out var name) ? name : "missing provider";
            foreach (var route in routes)
            {
                var chain = string.Join(" → ", (route.Fallbacks ?? []).Select(x => $"{Name(x.ProviderId)}/{x.Model}").Prepend($"{Name(route.ProviderId)}/{route.Model}"));
                var singleProvider = (route.Fallbacks ?? []).All(x => x.ProviderId == route.ProviderId);
                findings.Add(new($"route:{route.Stage}", (route.Fallbacks?.Count ?? 0) == 0 || singleProvider ? "Attention" : "Ready", $"{route.Stage} route",
                    (route.Fallbacks?.Count ?? 0) == 0 ? $"{chain}. No fallback: a provider outage stops this stage."
                        : singleProvider ? $"{chain}. Every fallback uses the same provider, so a provider outage still stops this stage."
                        : $"{chain}. Fallbacks are tried in order only when a target is unavailable before any output.", employeeUrl));
            }
            if (routes.All(x => x.Stage != "Default"))
                findings.Add(new("route:Default", "Info", "Default route", "Stages without their own route use the agent's own approved provider and model.", employeeUrl));
        }
        if (granted.Contains("platform.harness.judge") && routes.All(x => x.Stage != "Judgment"))
            findings.Add(new("route:Judgment", "Blocked", "Judgment route", "The agent may request advisory judgments, but no Judgment route is configured, so every judgment fails.", employeeUrl));
        if (policy.CompactContext && !granted.Contains("platform.harness.context.read"))
            findings.Add(new("context", "Attention", "Context retrieval", "Compaction is on, but historical context retrieval is not granted. Only exact duplicates shrink; large results are kept in full.", employeeUrl));
        return findings;
    }

    private void Audit(Guid installation, Guid actor, string kind, string detail)
    {
        var target = db.AgentInstallations.AsNoTracking().Single(x => x.Id == installation);
        var organization = Guid.TryParse(target.BusinessId, out var org) ? (Guid?)org : null;
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization,
            RecipientApplicationUserId = organization.HasValue ? null : target.OwnerApplicationUserId,
            EventType = CSweet.Contracts.Realtime.AppRealtimeEvents.HarnessChanged, Subject = installation.ToString("D"),
            DataJson = JsonSerializer.Serialize(new { installationId = installation }), OccurredAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow() });
        db.QueueAudit(new AuditEventWriteRequest(kind, OrganizationId: organization, EntityType: nameof(AgentInstallation), EntityId: installation,
            Summary: detail, Actor: new AuditActor("Human", ApplicationUserId: actor)));
    }
    private static HarnessSkillRevision Map(BusinessSkillRevision x) => new(x.Id, x.Name, x.Version, x.Digest, x.Instructions,
        JsonSerializer.Deserialize<string[]>(x.RequiredCapabilitiesJson) ?? [], x.Enabled, x.CreatedAt);
}
