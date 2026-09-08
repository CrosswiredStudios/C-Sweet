using System.Text.Json;

namespace CSweet.Contracts.Plugins;

// Native owner controls only. These are not capabilities available to plugin runtimes.
public sealed record ConnectorPolicyField(string Source, string Path);
public sealed record ConnectorPolicyFieldRule(ConnectorPolicyField Field, bool AllowAny,
    IReadOnlyList<JsonElement> AllowedValues, bool AllowOmission = false);
public sealed record ConnectorStandingPolicyDefinition(
    IReadOnlyList<ConnectorPolicyFieldRule> Fields,
    IReadOnlyList<int> DaysOfWeek,
    int StartMinute, int EndMinute,
    int MaximumActionsPerHour,
    DateTimeOffset NotBefore, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> EscalationTerms,
    ConnectorPolicyField? ScheduledAt = null,
    long? MaximumMediaBytes = null,
    IReadOnlyList<string>? AllowedMediaTypes = null,
    string TimeZoneId = "UTC");

public sealed record ApproveConnectorStandingPolicyRequest(Guid TemplatePlanId, string PlanHash,
    string ReviewHash, ConnectorStandingPolicyDefinition Definition, long? ExpectedRevision);
public sealed record ConnectorStandingPolicyReview(Guid TemplatePlanId, string PlanHash, string ReviewHash,
    string OperationDescription, string AccountName, IReadOnlyList<ConnectorPolicyField> MutableFields,
    bool CanUseStandingPolicy)
{
    public IReadOnlyList<ConnectorPolicyFieldReview> FieldReviews { get; init; } = [];
    public ConnectorPolicyMediaReview? Media { get; init; }
    public string RequesterName { get; init; } = "Requesting agent";
}
public sealed record ConnectorPolicyFieldReview(ConnectorPolicyField Field, string Label, string? Description,
    JsonElement? CurrentValue, bool CanOmit, IReadOnlyList<JsonElement> SuggestedValues);
public sealed record ConnectorPolicyMediaReview(string FileName, long SizeBytes, string ContentType);
public sealed record ConnectorStandingPolicySetup(ConnectorStandingPolicyReview? Review,
    ConnectorStandingPolicyView? Policy, string? UnavailableReason = null);
public sealed record RevokeConnectorStandingPolicyRequest(Guid PolicyId, long Revision);
public sealed record ConnectorStandingPolicyView(Guid Id, long Revision, string Status,
    string PolicyHash, Guid ApprovedByOrganizationUserId, DateTimeOffset ApprovedAt,
    ConnectorStandingPolicyDefinition Definition);
