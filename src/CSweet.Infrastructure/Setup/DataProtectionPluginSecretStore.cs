using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class DataProtectionPluginSecretStore(
    CSweetDbContext db,
    IDataProtectionProvider dataProtectionProvider) : IPluginSecretStore, IPluginSecretReferenceRebinder
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("CSweet.PluginSecrets.v1");

    public async Task StageAsync(Guid source,Guid target,IReadOnlyList<string> keys,CancellationToken ct)
    {
        if(source==target || source==Guid.Empty || target==Guid.Empty || keys.Count!=1 ||
            keys.Any(k=>!k.StartsWith("managed.context.",StringComparison.Ordinal)) ||
            db.Database.IsRelational() && db.Database.CurrentTransaction is null)throw new InvalidOperationException("A transactional managed reference move is required.");
        var rows=await db.PluginSecrets.Where(x=>x.PluginInstallationId==source).ToArrayAsync(ct);
        if(rows.Length!=keys.Count || rows.Any(x=>!keys.Contains(x.Key,StringComparer.Ordinal)) ||
            await db.PluginSecrets.AnyAsync(x=>x.PluginInstallationId==target,ct))throw new InvalidOperationException("The protected reference inventory changed.");
        foreach(var row in rows){row.PluginInstallationId=target;row.UpdatedAt=DateTimeOffset.UtcNow;}
        // No SaveChanges here: the caller atomically commits these encrypted reference rows
        // with the native ownership, exact grant digest and immutable original provenance.
    }

    public async Task SetAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("A plugin secret value is required.", nameof(value));
        if (!await db.AgentInstallations.AnyAsync(x => x.Id == installationId, cancellationToken))
            throw new InvalidOperationException("The plugin installation was not found.");

        var now = DateTimeOffset.UtcNow;
        var secret = await db.PluginSecrets.SingleOrDefaultAsync(
            x => x.PluginInstallationId == installationId && x.Key == key, cancellationToken);
        if (secret is null)
        {
            secret = new PluginSecret
            {
                Id = Guid.NewGuid(), PluginInstallationId = installationId, Key = key,
                CreatedAt = now
            };
            db.PluginSecrets.Add(secret);
        }
        secret.ProtectedValue = _protector.Protect(value);
        secret.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetAsync(Guid installationId, string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var protectedValue = await db.PluginSecrets.AsNoTracking()
            .Where(x => x.PluginInstallationId == installationId && x.Key == key)
            .Select(x => x.ProtectedValue)
            .SingleOrDefaultAsync(cancellationToken);
        return protectedValue is null ? null : _protector.Unprotect(protectedValue);
    }

    public async Task RemoveAsync(Guid installationId, string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var secret = await db.PluginSecrets.SingleOrDefaultAsync(
            x => x.PluginInstallationId == installationId && x.Key == key, cancellationToken);
        if (secret is null) return;
        db.PluginSecrets.Remove(secret);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 ||
            key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("Plugin secret keys may contain only letters, digits, '.', '-' and '_'.", nameof(key));
    }
}
