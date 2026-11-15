namespace CSweet.Domain.WorkManagement;

/// <summary>An accepted command's immutable content binding and result, independent of later state writes.</summary>
public sealed class DurableMutationReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PrincipalId { get; set; }
    public string ResourceKey { get; set; } = "";
    public string MutationKey { get; set; } = "";
    public string ContentDigest { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
