namespace CSweet.Contracts.Analytics;

public sealed record BenchmarkHire(string RoleKey, string DisplayName, Guid AgentDefinitionId,
    Guid PackageVersionId, string? ReportsToRoleKey = null);
public sealed record BenchmarkModel(Guid ProviderProfileId, string Model);
public sealed record BenchmarkVariant(string Name, BenchmarkModel DefaultModel,
    IReadOnlyDictionary<string, BenchmarkModel> RoleOverrides);
public sealed record BenchmarkCriterion(string Key, string Title, string Kind,
    string? Expected = null, decimal Weight = 1);
public sealed record BenchmarkSeed(string Title, string Content);
public sealed record BenchmarkBlueprint(string Name, string Goal, IReadOnlyList<BenchmarkHire> HiringPlan,
    IReadOnlyList<BenchmarkVariant> Variants, IReadOnlyList<BenchmarkCriterion> Criteria,
    IReadOnlyList<BenchmarkSeed> Inputs, string AssistanceMode = "Assisted",
    bool ApprovalsOnly = false, BenchmarkModel? Judge = null);
public sealed record CreateBenchmarkDefinitionRequest(BenchmarkBlueprint Blueprint, Guid? PreviousVersionId = null);
public sealed record BenchmarkDefinitionResponse(Guid Id, Guid FamilyId, int Version, string Digest,
    DateTimeOffset CreatedAt, BenchmarkBlueprint Blueprint);
public sealed record LaunchBenchmarkRequest(Guid DefinitionId, string IdempotencyKey,
    int Repetitions = 1, string Scheduling = "Sequential");
public sealed record BenchmarkAssessmentRequest(string Kind, string CriterionKey, decimal? Score,
    bool? Passed, string Rationale, IReadOnlyList<string> EvidenceReferences, string IdempotencyKey);
public sealed record BenchmarkAssessmentResponse(Guid Id, string Kind, string CriterionKey, decimal? Score,
    bool? Passed, string Rationale, IReadOnlyList<string> EvidenceReferences, Guid? ReviewerId,
    string EvaluatorVersion, DateTimeOffset CreatedAt);
public sealed record BenchmarkTrialResponse(Guid Id, Guid? OrganizationId, Guid? WorkstreamId,
    int VariantIndex, int Repetition, string VariantName, string Status, string EvaluationStatus,
    DateTimeOffset? StartedAt, DateTimeOffset? DeclaredCompletedAt, DateTimeOffset? FinishedAt,
    string? Detail, EfficiencyUsage DeliveryUsage, EfficiencyUsage EvaluationUsage, EfficiencyUsage TrailingUsage,
    string SubmissionJson, IReadOnlyList<BenchmarkAssessmentResponse> Assessments)
{
    public long? DeliveryTimeMs => StartedAt.HasValue && DeclaredCompletedAt.HasValue
        ? Math.Max(0, (long)(DeclaredCompletedAt.Value - StartedAt.Value).TotalMilliseconds) : null;
    public long HumanInterventions { get; init; }
    public long AgentHandoffs { get; init; }
    public long ToolOperations { get; init; }
    public EfficiencyUsage SetupUsage { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public string InteractionCoverage { get; init; } = "";
    public int FinalWorkforce { get; init; }
    public IReadOnlyList<string> Deviations { get; init; } = [];
}
public sealed record BenchmarkCampaignResponse(Guid Id, Guid DefinitionId, string Name,
    string Status, string Scheduling, int Repetitions, DateTimeOffset CreatedAt,
    IReadOnlyList<BenchmarkTrialResponse> Trials);
public sealed record BenchmarkCatalogResponse(IReadOnlyList<BenchmarkDefinitionResponse> Definitions,
    IReadOnlyList<BenchmarkCampaignResponse> Campaigns);
