namespace CSweet.Domain.Setup;

public sealed class SystemConfiguration
{
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public string? ConnectionServiceSelectionJson { get; set; }
    public Guid Id { get; set; }
    public bool IsFirstRunComplete { get; set; }
    public ExecutionOnboardingMode ExecutionOnboardingMode { get; set; }
    public Guid? DefaultChatProviderId { get; set; }
    public Guid? DefaultEmbeddingProviderId { get; set; }
    public Guid? PersonalAssistantProviderId { get; set; }
    public string? PersonalAssistantModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
