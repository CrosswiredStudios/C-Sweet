namespace CSweet.Domain.Setup;

public sealed class SystemConfiguration
{
    public Guid Id { get; set; }
    public bool IsFirstRunComplete { get; set; }
    public ExecutionOnboardingMode ExecutionOnboardingMode { get; set; }
    public Guid? DefaultChatProviderId { get; set; }
    public Guid? DefaultEmbeddingProviderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
