using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.BusinessOnboarding;

public sealed record CompleteBusinessOnboardingRequest(
    [Required] string BusinessName,
    string? Industry,
    string? MissionStatement,
    Guid ChiefAgentInstallationId,
    [StringLength(160)] string? ChiefDisplayName = null);
