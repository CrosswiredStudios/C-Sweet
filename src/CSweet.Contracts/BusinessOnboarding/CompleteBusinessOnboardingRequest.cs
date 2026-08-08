using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.BusinessOnboarding;

public sealed record CompleteBusinessOnboardingRequest(
    [Required] string BusinessName,
    string? Industry,
    string? MissionStatement,
    Guid ChiefAgentDefinitionId,
    [StringLength(160)] string? ChiefDisplayName = null);
