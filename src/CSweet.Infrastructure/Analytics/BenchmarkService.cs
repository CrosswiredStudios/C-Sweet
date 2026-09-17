using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Analytics;
using CSweet.Application.Core;
using CSweet.Application.Communications;
using CSweet.Contracts.Analytics;
using CSweet.Contracts.Core;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public sealed partial class BenchmarkService(CSweetDbContext db, TimeProvider clock,
    ICoreOrganizationService organizations, IOrganizationUserService employees,
    IAgentCommunicationOnboardingService onboarding, IChatTurnService turns,
    CSweet.AI.Providers.ILlmProviderFactory providers) : IBenchmarkService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static BenchmarkBlueprint Blueprint(BenchmarkDefinition definition) =>
        JsonSerializer.Deserialize<BenchmarkBlueprint>(definition.BlueprintJson, Json)!;

    public async Task<BenchmarkDefinitionResponse> CreateDefinitionAsync(CreateBenchmarkDefinitionRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var b = request.Blueprint;
        Validate(b);
        var definitions = await db.AgentDefinitions.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Configuration)
            .Where(x => b.HiringPlan.Select(h => h.AgentDefinitionId).Contains(x.Id)).ToListAsync(ct);
        foreach (var hire in b.HiringPlan)
        {
            var agent = definitions.SingleOrDefault(x => x.Id == hire.AgentDefinitionId);
            if (agent is null || !agent.IsAvailableForHire || agent.PackageVersionId != hire.PackageVersionId)
                throw new ArgumentException($"The agent for {hire.RoleKey} must be available at the selected package version.");
        }
        var models = b.Variants.SelectMany(v => v.RoleOverrides.Values.Prepend(v.DefaultModel))
            .Concat(b.Judge is null ? [] : new[] { b.Judge }).ToList();
        var ids = models.Select(x => x.ProviderProfileId).Distinct().ToArray();
        if (await db.LlmProviderProfiles.CountAsync(x => ids.Contains(x.Id), ct) != ids.Length)
            throw new ArgumentException("Every model must reference an existing provider profile.");
        var previous = request.PreviousVersionId.HasValue
            ? await db.BenchmarkDefinitions.SingleOrDefaultAsync(x => x.Id == request.PreviousVersionId, ct)
            : null;
        if (request.PreviousVersionId.HasValue && previous is null) throw new ArgumentException("Previous version not found.");
        var family = previous?.FamilyId ?? Guid.NewGuid();
        var version = previous is null ? 1 : 1 + await db.BenchmarkDefinitions.Where(x => x.FamilyId == family).MaxAsync(x => x.Version, ct);
        var blueprintJson = JsonSerializer.Serialize(b, Json);
        var manifest = JsonSerializer.Serialize(new
        {
            schema = "csweet.benchmark.v1", blueprint = b,
            agents = definitions.OrderBy(x => x.Id).Select(x => new { x.Id, x.PackageVersionId, x.PackageVersion!.Version,
                x.PackageVersion.ManifestDigest, x.PackageVersion.PackageDigest,
                x.DefaultMaxRuntimeSeconds, x.DefaultMemoryMb, x.DefaultCpuPercent,
                x.DefaultProvidedCapabilitiesJson, x.DefaultRequiredCapabilitiesJson,
                x.DefaultEventSubscriptionsJson, x.DefaultNetworkAccessJson,
                configurationDigest = ConfigurationDigest(x) }),
            platformVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(BenchmarkService).Assembly)?.InformationalVersion ?? typeof(BenchmarkService).Assembly.GetName().Version?.ToString()
        }, Json);
        var definition = new BenchmarkDefinition
        {
            Id = Guid.NewGuid(), FamilyId = family, Version = version, Name = b.Name.Trim(),
            BlueprintJson = blueprintJson, ManifestJson = manifest,
            Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))),
            CreatedBy = userId, CreatedAt = clock.GetUtcNow()
        };
        db.BenchmarkDefinitions.Add(definition);
        Audit("benchmark.definition.created", definition.Id, userId, null, $"Definition version {version}; digest {definition.Digest}");
        await db.SaveChangesAsync(ct);
        return ToResponse(definition);
    }

    internal static void Validate(BenchmarkBlueprint b)
    {
        if (b is null || b.HiringPlan is null || b.Variants is null || b.Criteria is null || b.Inputs is null)
            throw new ArgumentException("A complete benchmark blueprint is required.");
        if (b.HiringPlan.Any(x => x is null) || b.Variants.Any(x => x is null) || b.Criteria.Any(x => x is null) || b.Inputs.Any(x => x is null))
            throw new ArgumentException("Blueprint collections must not contain null entries.");
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 256 || string.IsNullOrWhiteSpace(b.Goal) || b.Goal.Length > 16000)
            throw new ArgumentException("Provide a name (256 characters maximum) and goal (16,000 characters maximum).");
        if (b.HiringPlan.Count is < 1 or > 30 || b.Variants.Count is < 2 or > 10 || b.Criteria.Count is < 1 or > 50 || b.Inputs.Count > 30)
            throw new ArgumentException("Specify 1–30 initial hires, 2–10 variants, 1–50 criteria and at most 30 inputs.");
        if (b.AssistanceMode is not ("Assisted" or "Unattended") || b.AssistanceMode == "Unattended" && b.ApprovalsOnly)
            throw new ArgumentException("Choose Assisted, Assisted with approvals only, or Unattended.");
        if (b.HiringPlan.Select(x => x.RoleKey).Distinct(StringComparer.Ordinal).Count() != b.HiringPlan.Count ||
            b.Criteria.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != b.Criteria.Count ||
            b.Variants.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != b.Variants.Count)
            throw new ArgumentException("Role keys, criterion keys and variant names must be unique.");
        var preceding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in b.HiringPlan)
        {
            if (string.IsNullOrWhiteSpace(h.RoleKey) || h.RoleKey.Length > 160 || string.IsNullOrWhiteSpace(h.DisplayName) || h.DisplayName.Length > 160 ||
                h.ReportsToRoleKey is not null && !preceding.Contains(h.ReportsToRoleKey))
                throw new ArgumentException("Hiring roles need names and must appear after their reporting manager.");
            preceding.Add(h.RoleKey);
        }
        foreach (var v in b.Variants)
        {
            if (v is null || v.RoleOverrides is null || string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 160 || v.RoleOverrides.Keys.Any(k => !preceding.Contains(k)))
                throw new ArgumentException("Variant names are required; overrides must reference a hiring role.");
            foreach (var m in v.RoleOverrides.Values.Prepend(v.DefaultModel)) ValidateModel(m);
        }
        if (b.Judge is not null) ValidateModel(b.Judge);
        foreach (var c in b.Criteria)
            if (string.IsNullOrWhiteSpace(c.Key) || c.Key.Length > 160 || string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 1024 || c.Weight <= 0 ||
                c.Kind is not ("ArtifactExists" or "ArtifactContains" or "SourceValidation" or "Rubric") ||
                c.Kind == "ArtifactContains" && string.IsNullOrWhiteSpace(c.Expected))
                throw new ArgumentException("Criteria need a unique key, title, positive weight and supported kind: ArtifactExists, ArtifactContains, SourceValidation or Rubric.");
        if (b.Inputs.Any(x => string.IsNullOrWhiteSpace(x.Title) || x.Title.Length > 256 || x.Content is null || x.Content.Length > 100_000) ||
            b.Inputs.Sum(x => x.Content.Length) > 500_000)
            throw new ArgumentException("Seed inputs must have titles and total no more than 500,000 characters.");
    }

    private static void ValidateModel(BenchmarkModel model)
    {
        if (model is null || model.ProviderProfileId == Guid.Empty || string.IsNullOrWhiteSpace(model.Model) || model.Model.Length > 512)
            throw new ArgumentException("A provider and model identifier are required.");
    }

    private static string ConfigurationDigest(CSweet.Domain.Setup.AgentDefinition agent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            agent.Configuration?.SchemaVersion, agent.Configuration?.SettingsJson,
            agent.DefaultMaxRuntimeSeconds, agent.DefaultMemoryMb, agent.DefaultCpuPercent,
            agent.DefaultProvidedCapabilitiesJson, agent.DefaultRequiredCapabilitiesJson,
            agent.DefaultEventSubscriptionsJson, agent.DefaultNetworkAccessJson
        }, Json))));

    public async Task<BenchmarkCampaignResponse> LaunchAsync(LaunchBenchmarkRequest request, Guid userId, CancellationToken ct = default)
    {
        if (request.Repetitions is < 1 or > 100 || request.Scheduling is not ("Sequential" or "Parallel") ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("Specify 1–100 repetitions, Sequential or Parallel scheduling, and an idempotency key.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"benchmark-launch:" + userId + ":" + request.IdempotencyKey}, 0))", ct);
        var existing = await db.BenchmarkCampaigns.SingleOrDefaultAsync(x => x.CreatedBy == userId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.DefinitionId != request.DefinitionId || existing.Repetitions != request.Repetitions || existing.Scheduling != request.Scheduling)
                throw new ArgumentException("The idempotency key belongs to a different launch.");
            return (await GetCampaignAsync(existing.Id, ct))!;
        }
        var definition = await db.BenchmarkDefinitions.SingleOrDefaultAsync(x => x.Id == request.DefinitionId, ct)
            ?? throw new ArgumentException("Benchmark definition not found.");
        var b = Blueprint(definition);
        var campaign = new BenchmarkCampaign { Id = Guid.NewGuid(), DefinitionId = definition.Id, CreatedBy = userId,
            IdempotencyKey = request.IdempotencyKey, Scheduling = request.Scheduling, Repetitions = request.Repetitions,
            CreatedAt = clock.GetUtcNow(), Revision = 1 };
        db.BenchmarkCampaigns.Add(campaign);
        var order = 0;
        for (var repetition = 1; repetition <= request.Repetitions; repetition++)
            foreach (var variant in Enumerable.Range(0, b.Variants.Count).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)))
                db.BenchmarkTrials.Add(new() { Id = Guid.NewGuid(), CampaignId = campaign.Id, VariantIndex = variant,
                    Repetition = repetition, ExecutionOrder = order++, NextRecoveryAt = clock.GetUtcNow(), Revision = 1 });
        Audit("benchmark.campaign.launched", campaign.Id, userId, null, $"Definition {definition.Id}; {order} trials; {request.Scheduling}");
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return (await GetCampaignAsync(campaign.Id, ct))!;
    }

    public async Task<BenchmarkCatalogResponse> GetAsync(CancellationToken ct = default)
    {
        var definitions = await db.BenchmarkDefinitions.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var ids = await db.BenchmarkCampaigns.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(25).Select(x => x.Id).ToListAsync(ct);
        var campaigns = new List<BenchmarkCampaignResponse>();
        foreach (var id in ids) if (await GetCampaignAsync(id, ct) is { } c) campaigns.Add(c);
        return new(definitions.Select(ToResponse).ToList(), campaigns);
    }

    public async Task<BenchmarkCampaignResponse?> GetCampaignAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.BenchmarkCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var d = await db.BenchmarkDefinitions.AsNoTracking().SingleAsync(x => x.Id == c.DefinitionId, ct);
        var b = Blueprint(d);
        var trials = await db.BenchmarkTrials.AsNoTracking().Where(x => x.CampaignId == id).OrderBy(x => x.ExecutionOrder).ToListAsync(ct);
        var results = new List<BenchmarkTrialResponse>();
        foreach (var t in trials)
        {
            var deliveryEnd = t.DeclaredCompletedAt ?? t.FinishedAt;
            var q = db.AgentRunLogs.AsNoTracking().ProviderCalls().Where(x => x.BenchmarkTrialId == t.Id);
            var deliveryQuery = q.Where(x => x.InvocationKind != "benchmark-evaluation" &&
                t.StartedAt != null && x.StartedAt >= t.StartedAt && (deliveryEnd == null || x.StartedAt <= deliveryEnd));
            var delivery = await deliveryQuery.SumAsync(ct);
            var setup = await q.Where(x => x.InvocationKind != "benchmark-evaluation" && (t.StartedAt == null || x.StartedAt < t.StartedAt)).SumAsync(ct);
            var evaluation = await q.Where(x => x.InvocationKind == "benchmark-evaluation").SumAsync(ct);
            var tail = await q.Where(x => x.InvocationKind != "benchmark-evaluation" && deliveryEnd != null && x.StartedAt > deliveryEnd).SumAsync(ct);
            var assessments = await db.BenchmarkAssessments.AsNoTracking().Where(x => x.TrialId == t.Id).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            var workforce = await db.CoreOrganizationUsers.CountAsync(x => t.OrganizationId != null && x.OrganizationId == t.OrganizationId && x.EmployeeType == EmployeeType.Agent && x.IsActive, ct);
            var models = await deliveryQuery.Select(x => x.Model).Where(x => x != null).Distinct().ToArrayAsync(ct);
            var expectedModels = b.Variants[t.VariantIndex].RoleOverrides.Values.Select(x => x.Model).Append(b.Variants[t.VariantIndex].DefaultModel.Model).ToHashSet();
            var changes = new List<string>();
            if (workforce != b.HiringPlan.Count && t.OrganizationId.HasValue) changes.Add($"Initial workforce {b.HiringPlan.Count}; current workforce {workforce}.");
            if (models.Any(x => !expectedModels.Contains(x!))) changes.Add("Unexpected model usage recorded; inspect inference activity.");
            var messages = db.CoreConversationMessages.Where(x => t.StartedAt != null &&
                x.Conversation!.OrganizationId == t.OrganizationId && x.SourceProvider != "Benchmark" &&
                x.CreatedAt >= t.StartedAt && (t.DeclaredCompletedAt == null || x.CreatedAt <= t.DeclaredCompletedAt) &&
                (t.FinishedAt == null || x.CreatedAt <= t.FinishedAt));
            var human = await messages.LongCountAsync(x => db.CoreOrganizationUsers.Any(u =>
                u.Id == x.SenderOrganizationUserId && u.OrganizationId == t.OrganizationId && u.EmployeeType == EmployeeType.Human), ct);
            var handoffs = await messages.LongCountAsync(x => x.DeliveryIntent == CommunicationDeliveryIntent.RequestResponse &&
                db.CoreOrganizationUsers.Any(u => u.Id == x.SenderOrganizationUserId && u.OrganizationId == t.OrganizationId && u.EmployeeType == EmployeeType.Agent), ct);
            var tools = await db.AuditEvents.LongCountAsync(x => x.OrganizationId == t.OrganizationId && t.StartedAt != null &&
                x.OccurredAt >= t.StartedAt && (t.DeclaredCompletedAt == null || x.OccurredAt <= t.DeclaredCompletedAt) &&
                (t.FinishedAt == null || x.OccurredAt <= t.FinishedAt) &&
                x.EventType == "agent.capability.started" && x.MetadataJson != null && x.MetadataJson.Contains("\"efficiencyKind\":\"Tool\""), ct);
            results.Add(new(t.Id, t.OrganizationId, t.WorkstreamId, t.VariantIndex, t.Repetition,
                b.Variants[t.VariantIndex].Name, t.Status, t.EvaluationStatus, t.StartedAt, t.DeclaredCompletedAt,
                t.FinishedAt, t.Detail, delivery, evaluation, tail, t.SubmissionJson, assessments.Select(AssessmentResponse).ToList())
            { FinalWorkforce = workforce, HumanInterventions = human, AgentHandoffs = handoffs, ToolOperations = tools,
                SetupUsage = setup, InteractionCoverage = "Human messages from identified humans; agent request-response messages; non-LLM MCP tool attempts. Audit-backed tool totals may lag. These categories can overlap and are not added to model calls.", Deviations = changes });
        }
        return new(c.Id, c.DefinitionId, d.Name, c.Status, c.Scheduling, c.Repetitions, c.CreatedAt, results);
    }

    public async Task CancelAsync(Guid trialId, CancellationToken ct = default)
    {
        var trial = await db.BenchmarkTrials.SingleOrDefaultAsync(x => x.Id == trialId, ct) ?? throw new ArgumentException("Trial not found.");
        if (trial.Status is "Cancelled" or "DeclaredComplete" or "Failed") return;
        trial.Status = "Cancelled"; trial.Detail = "Cancelled by an administrator; partial results retained.";
        trial.FinishedAt = clock.GetUtcNow(); trial.Revision++;
        await StopWorkAsync(trial, ct);
        if (!await db.BenchmarkTrials.AnyAsync(x => x.CampaignId == trial.CampaignId && x.Id != trial.Id &&
            x.Status != "DeclaredComplete" && x.Status != "Failed" && x.Status != "Cancelled", ct))
        {
            var campaign = await db.BenchmarkCampaigns.SingleAsync(x => x.Id == trial.CampaignId, ct);
            campaign.Status = "DeliveryFinished"; campaign.Revision++;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<BenchmarkAssessmentResponse> AssessAsync(Guid trialId, BenchmarkAssessmentRequest request, Guid userId, CancellationToken ct = default)
    {
        var trial = await db.BenchmarkTrials.SingleOrDefaultAsync(x => x.Id == trialId, ct) ?? throw new ArgumentException("Trial not found.");
        if (trial.DeclaredCompletedAt is null) throw new ArgumentException("A frozen submission is required before evaluation.");
        if (request.Kind != "Human" || request.Score is null or < 0 or > 5 || request.Passed is not null ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160 ||
            string.IsNullOrWhiteSpace(request.Rationale) || request.Rationale.Length > 8000 || request.EvidenceReferences is null || request.EvidenceReferences.Count > 50 || request.EvidenceReferences.Any(x => x is null || x.Length > 512))
            throw new ArgumentException("Human reviews require a 0–5 score, rationale, evidence references and idempotency key.");
        var campaign = await db.BenchmarkCampaigns.SingleAsync(x => x.Id == trial.CampaignId, ct);
        var definition = await db.BenchmarkDefinitions.SingleAsync(x => x.Id == campaign.DefinitionId, ct);
        if (!Blueprint(definition).Criteria.Any(x => x.Key == request.CriterionKey && x.Kind == "Rubric"))
            throw new ArgumentException("Review criterion is not in this definition's rubric.");
        var existing = await db.BenchmarkAssessments.SingleOrDefaultAsync(x => x.TrialId == trialId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.ReviewerId != userId || existing.CriterionKey != request.CriterionKey || existing.Score != request.Score || existing.Rationale != request.Rationale ||
                existing.EvidenceReferencesJson != JsonSerializer.Serialize(request.EvidenceReferences, Json))
                throw new ArgumentException("The review key has already been used for different content.");
            return AssessmentResponse(existing);
        }
        var review = new BenchmarkAssessment { Id = Guid.NewGuid(), TrialId = trialId, Kind = "Human", CriterionKey = request.CriterionKey,
            Score = request.Score, Rationale = request.Rationale, EvidenceReferencesJson = JsonSerializer.Serialize(request.EvidenceReferences, Json),
            IdempotencyKey = request.IdempotencyKey, ReviewerId = userId, EvaluatorVersion = $"rubric:{definition.Digest}", CreatedAt = clock.GetUtcNow() };
        db.BenchmarkAssessments.Add(review); trial.Revision++;
        db.BenchmarkWakes.Add(new() { Id = Guid.NewGuid(), TrialId = trial.Id, CreatedAt = clock.GetUtcNow() });
        Audit("benchmark.assessment.created", review.Id, userId, trial.OrganizationId, $"Human review for {trial.Id}; criterion {review.CriterionKey}");
        await db.SaveChangesAsync(ct);
        return AssessmentResponse(review);
    }

    private static BenchmarkDefinitionResponse ToResponse(BenchmarkDefinition d) => new(d.Id, d.FamilyId, d.Version, d.Digest, d.CreatedAt, Blueprint(d));
    private void Audit(string eventType, Guid resourceId, Guid userId, Guid? organization, string summary) =>
        db.QueueAudit(new CSweet.Application.Setup.AuditEventWriteRequest(eventType, "Benchmark", OrganizationId: organization,
            EntityType: "Benchmark", EntityId: resourceId, Summary: summary, OccurredAt: clock.GetUtcNow(),
            Actor: new("Human", true, ApplicationUserId: userId), UseAmbientOrganization: false));
    private static BenchmarkAssessmentResponse AssessmentResponse(BenchmarkAssessment x) => new(x.Id, x.Kind, x.CriterionKey, x.Score,
        x.Passed, x.Rationale, JsonSerializer.Deserialize<string[]>(x.EvidenceReferencesJson, Json) ?? [], x.ReviewerId, x.EvaluatorVersion, x.CreatedAt);
}
