using System.Text.Json;

namespace CSweet.Contracts.Core;

public sealed record ProjectPortfolioResponse(
    DateTimeOffset GeneratedAt,
    int Total,
    int Active,
    IReadOnlyList<ProjectPortfolioItem> Projects);

public sealed record ProjectPortfolioItem(
    Guid Id,
    string Name,
    string Outcome,
    string Status,
    string LifecycleStage,
    string? ProfileKey,
    int? ProfileVersion,
    Guid? AccountableManagerOrganizationUserId,
    DateTimeOffset? TargetDate,
    decimal? BudgetAmount,
    string? BudgetCurrency,
    long Revision,
    DateTimeOffset UpdatedAt,
    int ActiveTeams,
    int Boards,
    int OpenItems,
    int PendingGates,
    int OpenDecisions,
    string? LatestBuildStatus);

public sealed record ProjectHealthSummary(
    int OpenItems,
    int BlockedItems,
    int PendingGates,
    int OpenDecisions,
    int DocumentsInReview,
    int FailedBuilds,
    int BlockingValidations,
    bool ReleaseReady);

/// <summary>
/// A typed, domain-neutral resource shown in a project inspection panel. Metadata stays resource-
/// specific, while identity, provenance, status, revision, attribution, and deep-link fields are
/// stable across profiles.
/// </summary>
public sealed record ProjectInspectionResource(
    Guid Id,
    string ResourceType,
    string? TypeKey,
    string Title,
    string Status,
    long? Revision,
    string? Sha256,
    Guid? ActorOrganizationUserId,
    Guid? ProviderInstallationId,
    string? ProviderPackageId,
    string? ProviderPackageVersion,
    DateTimeOffset? OccurredAt,
    string DeepLink,
    JsonElement Metadata);

public sealed record ProjectAuditEntry(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string EventType,
    string Category,
    string Outcome,
    string? EntityType,
    Guid? EntityId,
    string Summary,
    string ActorKind,
    Guid? ActorOrganizationUserId,
    string? ActorDisplayName,
    string? ActorAgentId,
    Guid? ActorInstallationId,
    string? CorrelationId,
    Guid? TraceId,
    Guid? ParentEventId,
    string RecordHash);

public sealed record ProjectInspectionResponse(
    ProjectInspectionResource Project,
    ProjectHealthSummary Health,
    IReadOnlyList<ProjectInspectionResource> Teams,
    IReadOnlyList<ProjectInspectionResource> Work,
    IReadOnlyList<ProjectInspectionResource> Documents,
    IReadOnlyList<ProjectInspectionResource> Communications,
    IReadOnlyList<ProjectInspectionResource> Governance,
    IReadOnlyList<ProjectInspectionResource> Evidence,
    IReadOnlyList<ProjectAuditEntry> Audit,
    DateTimeOffset GeneratedAt);
