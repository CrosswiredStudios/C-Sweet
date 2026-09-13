namespace CSweet.TrustedServices;

/// <summary>Core resolves the repository from a live owned personal ticket. No Git coordinates or credentials.</summary>
public sealed record AgentBrokerPersonalRepositoryRequest(Guid OrganizationId, Guid AgentInstallationId,
    Guid WorkItemId, string IdempotencyKey);
