namespace CSweet.Contracts.Agents;

public sealed record CapabilityApprovalResponse(Guid Id, Guid InstallationId, string Capability,
    string PayloadJson, string Risk, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DecideCapabilityApprovalRequest(bool Approve);
public sealed record StoredCapabilityResult(bool Succeeded, string? ContentType, string? Error, byte[] Payload);
public sealed record CapabilityAdmission(Guid ApprovalId, string Status, string? ResultsJson = null);
