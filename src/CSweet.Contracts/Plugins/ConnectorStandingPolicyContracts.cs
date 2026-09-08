using System.Text.Json;

namespace CSweet.Contracts.Plugins;

// Native owner controls only. These are not capabilities available to plugin runtimes.
public sealed record ConnectorPolicyField(string Source, string Path);
public sealed record ConnectorPolicyFieldRule(ConnectorPolicyField Field, bool AllowAny,
    IReadOnlyList<JsonElement> AllowedValues, bool AllowOmission = false);
public sealed record ConnectorStandingPolicyDefinition(
    IReadOnlyList<ConnectorPolicyFieldRule> Fields,
    IReadOnlyList<int> AllowedUtcDays,
    int StartUtcMinute, int EndUtcMinute,
    int MaximumActionsPerHour,
    DateTimeOffset NotBefore, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> EscalationTerms,
    ConnectorPolicyField? ScheduledAt = null,
    long? MaximumMediaBytes = null,
    IReadOnlyList<string>? AllowedMediaTypes = null);

public sealed record ApproveConnectorStandingPolicyRequest(Guid TemplatePlanId, string PlanHash,
    string ReviewHash, ConnectorStandingPolicyDefinition Definition, long? ExpectedRevision);
public sealed record ConnectorStandingPolicyReview(Guid TemplatePlanId, string PlanHash, string ReviewHash,
    string OperationDescription, string AccountName, IReadOnlyList<ConnectorPolicyField> MutableFields,
    bool CanUseStandingPolicy);
public sealed record ConnectorStandingPolicyView(Guid Id, long Revision, string Status,
    string PolicyHash, Guid ApprovedByOrganizationUserId, DateTimeOffset ApprovedAt,
    ConnectorStandingPolicyDefinition Definition);
