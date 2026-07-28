namespace CSweet.Domain.Core;

public enum OrganizationStatus { Draft, Active, Paused, Archived }
public enum WorkstreamStatus { Proposed, Approved, Active, Blocked, Completed, Cancelled }
public enum ProposalStatus { Pending, Approved, Rejected, Cancelled }
public enum BudgetScopeType { Organization, Workstream, Employee, Task }
public enum BudgetReservationStatus { Active, Committed, Released, Expired }

public sealed class BusinessProfile
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string? BusinessType { get; set; }
    public string? Description { get; set; }
    public string? TargetCustomersJson { get; set; }
    public string? OfferingsJson { get; set; }
    public string? RevenueModel { get; set; }
    public string? JurisdictionsJson { get; set; }
    public string? OperatingStyle { get; set; }
    public string? ToolsJson { get; set; }
    public string? RiskPreference { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string ProvenanceJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public decimal Completeness { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Organization? Organization { get; set; }
}

public sealed class FinancialOperatingProfile
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string BaseCurrency { get; set; } = "USD";
    public decimal? RevenueTarget { get; set; }
    public decimal? ProfitTarget { get; set; }
    public decimal? OwnerCompensationTarget { get; set; }
    public decimal? MinimumRunwayMonths { get; set; }
    public decimal? MaximumMonthlyWorkforceSpend { get; set; }
    public decimal? PerEngagementCap { get; set; }
    public int? MaximumConcurrentHires { get; set; }
    public string RoutingPreference { get; set; } = "Balanced";
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public Organization? Organization { get; set; }
}

public sealed class BusinessDiscoveryAssessment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string ConfirmedFactsJson { get; set; } = "{}";
    public string AssumptionsJson { get; set; } = "[]";
    public string MissingQuestionsJson { get; set; } = "[]";
    public string SelectedPatternsJson { get; set; } = "[]";
    public string? NextQuestion { get; set; }
    public decimal Confidence { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LeadershipAssignment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public string PositionKey { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
}

public sealed class Workstream
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? StrategicObjectiveId { get; set; }
    public Guid? AccountableManagerOrganizationUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string SuccessCriteriaJson { get; set; } = "[]";
    public string LifecycleStage { get; set; } = "Idea";
    public string ManagerTitle { get; set; } = "Product Manager";
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public string RisksJson { get; set; } = "[]";
    public WorkstreamStatus Status { get; set; } = WorkstreamStatus.Proposed;
    public DateTimeOffset? TargetDate { get; set; }
    public decimal? BudgetAmount { get; set; }
    public string? BudgetCurrency { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ActionProposal
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string RiskClass { get; set; } = "ReversibleWrite";
    public string IdempotencyKey { get; set; } = string.Empty;
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class Budget
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public BudgetScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public decimal LimitAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class BudgetReservation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BudgetId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Purpose { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public BudgetReservationStatus Status { get; set; } = BudgetReservationStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ManagementCycle
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string DailyCheckInLocalTime { get; set; } = "09:00";
    public string DailyDueLocalTime { get; set; } = "11:00";
    public string WeeklyReviewDay { get; set; } = "Friday";
    public string WeeklyReviewLocalTime { get; set; } = "15:00";
    public string QuietHoursStart { get; set; } = "18:00";
    public string QuietHoursEnd { get; set; } = "08:00";
    public DateTimeOffset? NextReviewAt { get; set; }
    public bool ExecutiveBriefingEnabled { get; set; } = true;
    public bool StartupBriefingEnabled { get; set; } = true;
    public string ExecutiveBriefingCadence { get; set; } = "Weekdays";
    public string ExecutiveBriefingWeeklyDay { get; set; } = "Friday";
    public string ExecutiveBriefingLocalTime { get; set; } = "09:00";
    public DateTimeOffset? NextExecutiveBriefingAt { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class BusinessPattern
{
    public Guid Id { get; set; }
    public string PatternKey { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string LifecycleStage { get; set; } = string.Empty;
    public string ApplicableBusinessTypesJson { get; set; } = "[]";
    public string JurisdictionsJson { get; set; } = "[]";
    public string WorkstreamsJson { get; set; } = "[]";
    public string TeamRecipeJson { get; set; } = "[]";
    public string RisksJson { get; set; } = "[]";
    public string FinancialConsiderationsJson { get; set; } = "[]";
    public string Provenance { get; set; } = "BuiltIn";
    public bool IsVerified { get; set; } = true;
    public DateTimeOffset ReviewDate { get; set; }
}

public sealed class WorkforcePlan
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid RequestingInstallationId { get; set; }
    public Guid? RecommendedCandidateId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Objective { get; set; } = string.Empty;
    public int Priority { get; set; } = 50;
    public string? RoleKey { get; set; }
    public int Headcount { get; set; } = 1;
    public Guid? SourceResourceChangeRequestId { get; set; }
    public string AssignmentsJson { get; set; } = "[]";
    public string RejectedAlternativesJson { get; set; } = "[]";
    public decimal? EstimatedMonthlyCost { get; set; }
    public string? Currency { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class WorkforceCandidate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? WorkforcePlanId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ExternalCandidateId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CapabilitiesJson { get; set; } = "[]";
    public decimal Score { get; set; }
    public decimal? EstimatedCost { get; set; }
    public string? Currency { get; set; }
    public bool IsHuman { get; set; }
    public bool IsAvailable { get; set; }
    public string ExplanationJson { get; set; } = "[]";
}

public sealed class ResourceNeed
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid RequestedByOrganizationUserId { get; set; }
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public string BusinessOutcome { get; set; } = string.Empty;
    public string Urgency { get; set; } = "Normal";
    public bool MandatoryHuman { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StaffingActionProposal
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkforcePlanId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string CandidateSource { get; set; } = string.Empty;
    public string CandidateId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public Guid RequestingInstallationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? ApprovedByOrganizationUserId { get; set; }
    public Guid? ResultOrganizationUserId { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class Responsibility
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset? ReviewAt { get; set; }
}

public sealed class ManagementCheckInRequestRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ManagementCycleId { get; set; }
    public Guid RequestedByOrganizationUserId { get; set; }
    public Guid RequestedFromOrganizationUserId { get; set; }
    public string CheckInType { get; set; } = "Manager";
    public string TopicsJson { get; set; } = "[]";
    public string? IdempotencyKey { get; set; }
    public string? TriggerType { get; set; }
    public string Status { get; set; } = "Pending";
    public int DispatchAttempts { get; set; }
    public DateTimeOffset? LastDispatchedAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

public sealed class ManagementStatusReportRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ManagementCheckInRequestId { get; set; }
    public Guid ReporterOrganizationUserId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string OutcomesJson { get; set; } = "[]";
    public string BlockersJson { get; set; } = "[]";
    public string RisksJson { get; set; } = "[]";
    public string DecisionsJson { get; set; } = "[]";
    public string? Markdown { get; set; }
    public string ImmediateActionsJson { get; set; } = "[]";
    public string ConversationTopicsJson { get; set; } = "[]";
    public string Severity { get; set; } = "Important";
    public DateTimeOffset ReportedAt { get; set; }
}

public sealed class ExecutiveBriefingDeliveryRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ManagementCheckInRequestId { get; set; }
    public Guid ManagementStatusReportId { get; set; }
    public Guid RecipientOrganizationUserId { get; set; }
    public string Channel { get; set; } = "InApp";
    public string Status { get; set; } = "Pending";
    public string PayloadJson { get; set; } = "{}";
    public Guid? ConversationId { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public Guid? NotificationId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ResourceNeedReportRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ManagementStatusReportId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid ReporterOrganizationUserId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string BusinessOutcome { get; set; } = string.Empty;
    public string Urgency { get; set; } = "Normal";
    public string Evidence { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public DateTimeOffset ReportedAt { get; set; }
}
