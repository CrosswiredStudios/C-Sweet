namespace CSweet.Domain.Core;

public sealed class WorkstreamProfileDefinitionRecord
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string MetadataSchemaJson { get; set; } = "{}";
    public string LifecyclePolicyKey { get; set; } = string.Empty;
    public string DefaultBoardProfileKey { get; set; } = string.Empty;
    public string? AuthorityPolicyKey { get; set; }
    public string Status { get; set; } = "Active";
    public string ProviderPackageId { get; set; } = string.Empty;
    public string ProviderPackageVersion { get; set; } = string.Empty;
    public string DefinitionDigest { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkstreamTeamAssignmentRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid TeamId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class WorkstreamSupervisionAssignment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid SupervisorOrganizationUserId { get; set; }
    public string RoleKey { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class WorkstreamAuthorityEnvelopeRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public decimal? MaximumBudgetVariance { get; set; }
    public int? MaximumScheduleVarianceDays { get; set; }
    public string AuthorizedStaffingRoleKeysJson { get; set; } = "[]";
    public string HumanRequiredActionKeysJson { get; set; } = "[]";
    public string AgentAuthorizedActionKeysJson { get; set; } = "[]";
    public DateTimeOffset? ExpiresAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkstreamMilestoneRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LifecycleStage { get; set; } = string.Empty;
    public DateTimeOffset? TargetDate { get; set; }
    public string RequiredEvidenceTypeKeysJson { get; set; } = "[]";
    public string RequiredReviewerRoleKeysJson { get; set; } = "[]";
    public int Position { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class WorkstreamGateRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid? MilestoneId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LifecycleStage { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string RequiredEvidenceTypeKeysJson { get; set; } = "[]";
    public string RequiredReviewerRoleKeysJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "[]";
    public string FindingsJson { get; set; } = "[]";
    public string? SubmissionSummary { get; set; }
    public string? DecisionRationale { get; set; }
    public Guid? SubmittedByOrganizationUserId { get; set; }
    public Guid? DecidedByOrganizationUserId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class WorkstreamDecisionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string TypeKey { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string AuthorityRuleKey { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public string RecommendedOptionId { get; set; } = string.Empty;
    public string? SelectedOptionId { get; set; }
    public string EvidenceJson { get; set; } = "[]";
    public string? TypeDataJson { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Rationale { get; set; }
    public string BlockingImpact { get; set; } = string.Empty;
    public Guid RequestedByOrganizationUserId { get; set; }
    public Guid? RequestedByInstallationId { get; set; }
    public Guid? DecidedByOrganizationUserId { get; set; }
    public Guid? SupersedesDecisionId { get; set; }
    public Guid? SupersededByDecisionId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
