namespace CSweet.Application.Setup;

public interface IAgentAttentionInvalidationService
{
    Task InvalidateAsync(
        IReadOnlyCollection<Guid> installationIds,
        string triggerCategory,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task InvalidateManagersAsync(
        Guid organizationId,
        string triggerCategory,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}
