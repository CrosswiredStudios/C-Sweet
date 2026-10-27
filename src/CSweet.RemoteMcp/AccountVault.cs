using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.RemoteMcp;

// Trusted host types only. Never reference this assembly from a workload or expose these methods as tools.
public readonly record struct AccountScope(Guid Owner, Guid Business, Guid ProviderInstallation)
{
    internal string Purpose => $"{Owner:N}.{Business:N}.{ProviderInstallation:N}";
    internal void Validate()
    {
        if (Owner == Guid.Empty || Business == Guid.Empty || ProviderInstallation == Guid.Empty)
            throw new UnauthorizedAccessException("An authenticated account scope is required.");
    }
}
public sealed record AccountReference(Guid Id, long Generation);
public sealed record AccountGrant(Guid ConsumerInstallation, string Capability);
public sealed record ProtectedAccount(Guid Id, long Revision, string Ciphertext);

/// <summary>Implement using durable storage with atomic compare-and-swap. Scope must also be checked by the host's current-user authorization layer.</summary>
public interface IAccountRepository
{
    Task<ProtectedAccount?> ReadAsync(Guid id, CancellationToken ct);
    Task<bool> CompareExchangeAsync(Guid id, long? expectedRevision, ProtectedAccount next, CancellationToken ct);
}

public sealed class AccountVault(IAccountRepository repository, IDataProtectionProvider protection)
{
    private sealed record State(string Key, bool Revoked, AccountGrant[] Grants);
    private IDataProtector Protector(AccountScope scope, Guid id) => protection.CreateProtector("CSweet.RemoteMcp.Account.v1", scope.Purpose, id.ToString("N"));
    private async Task<(ProtectedAccount Record, State State)> Read(AccountScope scope, Guid id, CancellationToken ct)
    {
        scope.Validate();
        var record = await repository.ReadAsync(id, ct) ?? throw Denied();
        try { return (record, JsonSerializer.Deserialize<State>(Protector(scope, id).Unprotect(record.Ciphertext)) ?? throw Denied()); }
        catch (System.Security.Cryptography.CryptographicException) { throw Denied(); }
        catch (JsonException) { throw Denied(); }
    }
    public async Task<AccountReference> ConnectAsync(AccountScope scope, string key, CancellationToken ct = default)
    {
        scope.Validate(); ValidateKey(key);
        var id = Guid.NewGuid();
        var record = new ProtectedAccount(id, 1, Protector(scope, id).Protect(JsonSerializer.Serialize(new State(key, false, []))));
        if (!await repository.CompareExchangeAsync(id, null, record, ct)) throw Denied();
        return new(id, 1);
    }
    public Task<AccountReference> RotateAsync(AccountScope scope, AccountReference account, string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        // Rotation deliberately clears grants. A human must review and grant the replacement credential.
        return Change(scope, account, _ => new State(key, false, []), ct);
    }
    public Task<AccountReference> RevokeAsync(AccountScope scope, AccountReference account, CancellationToken ct = default) =>
        Change(scope, account, _ => new State("", true, []), ct);
    public Task<AccountReference> GrantAsync(AccountScope scope, AccountReference account, Guid consumer, CancellationToken ct = default)
    {
        if (consumer == Guid.Empty) throw Denied();
        return Change(scope, account, state => state.Revoked ? throw Denied() : state with
        { Grants = [new(consumer, RealtyPolicy.Search), new(consumer, RealtyPolicy.Lookup)] }, ct);
    }
    public async Task<(AccountReference Reference, bool Revoked, IReadOnlyList<AccountGrant> Grants)> InspectAsync(AccountScope scope, Guid id, CancellationToken ct = default)
    {
        var (record, state) = await Read(scope, id, ct);
        return (new(id, record.Revision), state.Revoked, state.Grants);
    }
    private async Task<AccountReference> Change(AccountScope scope, AccountReference account, Func<State, State> change, CancellationToken ct)
    {
        var (record, state) = await Read(scope, account.Id, ct);
        if (record.Revision != account.Generation) throw Denied();
        var next = new ProtectedAccount(record.Id, checked(record.Revision + 1), Protector(scope, record.Id).Protect(JsonSerializer.Serialize(change(state))));
        if (!await repository.CompareExchangeAsync(record.Id, record.Revision, next, ct)) throw Denied();
        return new(next.Id, next.Revision);
    }
    internal async Task<string> RequireAsync(AccountScope scope, AccountReference account, Guid consumer, string capability, CancellationToken ct)
    {
        var (record, state) = await Read(scope, account.Id, ct);
        if (record.Revision != account.Generation || state.Revoked || !state.Grants.Contains(new(consumer, capability))) throw Denied();
        return state.Key;
    }
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length is < 16 or > 256 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '=' and not '+'))
            throw new ArgumentException("The API key format is invalid.");
    }
    private static UnauthorizedAccessException Denied() => new("The account connection or grant is unavailable. Reconnect or review access in native settings.");
}
