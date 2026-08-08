namespace CSweet.Contracts.BusinessOnboarding;

public sealed record CompleteChiefSetupRequest(Guid AgentDefinitionId);

public sealed record CompleteChiefSetupResponse(
    Guid OrganizationId,
    Guid ChiefOrganizationUserId,
    IReadOnlyList<string> ReadinessWarnings,
    string NextRoute);

public sealed record ChiefSetupActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string? Message,
    CompleteChiefSetupResponse? Setup = null);
