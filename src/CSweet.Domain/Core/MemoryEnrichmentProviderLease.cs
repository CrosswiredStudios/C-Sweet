namespace CSweet.Domain.Core;

/// <summary>One expiring background enrichment reservation per provider, shared by worker replicas.</summary>
public sealed class MemoryEnrichmentProviderLease
{
    public Guid ProviderId { get; set; }
    public Guid LeaseToken { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
