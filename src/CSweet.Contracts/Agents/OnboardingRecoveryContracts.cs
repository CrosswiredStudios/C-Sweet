namespace CSweet.Contracts.Agents;

public sealed record OnboardingRecoveryReview(Guid EventId, Guid WorkId, int CompletedAttempts,
    int PermittedNextAttempt, DateTimeOffset ReviewExpiresAt, string ReviewToken);
public sealed record RecoverAgentOnboardingRequest(Guid OrganizationId, string ReviewToken);
public sealed record OnboardingRecoveryReceipt(Guid RecoveryId, Guid EventId, Guid WorkId,
    int PermittedAttempt, DateTimeOffset RequestedAt, DateTimeOffset DeadlineAt);
