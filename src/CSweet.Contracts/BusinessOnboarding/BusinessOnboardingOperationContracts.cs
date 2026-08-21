using System.ComponentModel.DataAnnotations;
using CSweet.Contracts.Agents;

namespace CSweet.Contracts.BusinessOnboarding;

public static class BusinessOnboardingOperationStatuses
{
    public const string Starting = "Starting";
    public const string InstallingAgent = "InstallingAgent";
    public const string BuildingAgent = "BuildingAgent";
    public const string CreatingBusiness = "CreatingBusiness";
    public const string Succeeded = "Succeeded";
    public const string NeedsSetup = "NeedsSetup";
    public const string Failed = "Failed";

    public static bool IsActive(string status) => status is
        Starting or InstallingAgent or BuildingAgent or CreatingBusiness;
}

public sealed record StartBusinessOnboardingRequest(
    [Required, StringLength(256)] string BusinessName,
    [StringLength(160)] string? Industry,
    [StringLength(4096)] string? MissionStatement,
    [Required] Guid ChiefAgentPackageVersionId,
    [StringLength(160)] string? ChiefDisplayName,
    [Required, StringLength(160)] string IdempotencyKey,
    [Required] InstallAgentRequest ChiefAgentInstallRequest);

public sealed record BusinessOnboardingOperationResponse(
    Guid Id,
    string BusinessName,
    string ChiefAgentName,
    string Status,
    string Phase,
    string Detail,
    int CompletedBuildSteps,
    int TotalBuildSteps,
    Guid? ChiefAgentDefinitionId,
    Guid? OrganizationId,
    string? ActionUri,
    string? Error,
    DateTimeOffset UpdatedAt);
