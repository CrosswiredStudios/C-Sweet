namespace CSweet.Application.Setup;

/// <summary>Host-only transactional reference move. No decryption or raw-value export. The caller
/// owns the same database transaction as the account/installation adoption.</summary>
public interface IPluginSecretReferenceRebinder
{
    Task StageAsync(Guid source,Guid target,IReadOnlyList<string> keys,CancellationToken ct);
}
