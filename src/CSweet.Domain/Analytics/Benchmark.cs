namespace CSweet.Domain.Analytics;

public sealed class BenchmarkDefinition
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public string BlueprintJson { get; set; } = "{}";
    public string ManifestJson { get; set; } = "{}";
    public string Digest { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BenchmarkCampaign
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid CreatedBy { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Scheduling { get; set; } = "Sequential";
    public string Status { get; set; } = "Running";
    public int Repetitions { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class BenchmarkTrial
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public int VariantIndex { get; set; }
    public int Repetition { get; set; }
    public int ExecutionOrder { get; set; }
    public string Status { get; set; } = "Pending";
    public string EvaluationStatus { get; set; } = "Pending";
    public string? Detail { get; set; }
    public string SubmissionJson { get; set; } = "{}";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? DeclaredCompletedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset NextRecoveryAt { get; set; }
    public long Revision { get; set; }
}

public sealed class BenchmarkAssessment
{
    public Guid Id { get; set; }
    public Guid TrialId { get; set; }
    public string Kind { get; set; } = "";
    public string CriterionKey { get; set; } = "";
    public decimal? Score { get; set; }
    public bool? Passed { get; set; }
    public string Rationale { get; set; } = "";
    public string EvidenceReferencesJson { get; set; } = "[]";
    public Guid? ReviewerId { get; set; }
    public string EvaluatorVersion { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Wake hint committed with a trial or its observed workstream. The worker rereads state.</summary>
public sealed class BenchmarkWake
{
    public Guid Id { get; set; }
    public Guid? TrialId { get; set; }
    public Guid? OrganizationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
