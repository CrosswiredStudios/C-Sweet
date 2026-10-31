using System.Text.Json;
using CSweet.RemoteMcp;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.RemoteMcp.Probe;

// Trusted-host test utility, never deployed as a workload or exposed as a model tool.
public sealed record ProbeOutcome(string Status, int SearchCount, bool LookupSucceeded, bool RevocationVerified, string? FailureCategory = null, string? FailureStage = null, int? HttpStatus = null);
/// <summary>
/// One-shot RealtyLeads search/lookup probe. The endpoint, header and tool mapping come only from the reviewed
/// provider manifest supplied by the operator; this tool never constructs its own transport policy.
/// </summary>
public sealed class ProbeRunner
{
    public const string Search = "realtyleads.inheritance.search.v1";
    public const string Lookup = "realtyleads.inheritance.lookup.v1";
    public async Task<ProbeOutcome> RunAsync(ReviewedRemoteMcpProfile profile, string key, CancellationToken ct,
        Func<AccountVault, RemoteMcpBroker>? syntheticBrokerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Declares(Search) || !profile.Declares(Lookup))
            return new("failed", 0, false, false, "ProfileRejected");
        var repository = new MemoryRepository();
        var vault = new AccountVault(repository, new EphemeralDataProtectionProvider());
        var scope = new AccountScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var consumer = Guid.NewGuid();
        AccountReference? account = null;
        string? category = null, stage = null; int? httpStatus = null;
        var status = "failed"; var count = 0; var lookup = false; var revoked = false;
        var broker = syntheticBrokerFactory?.Invoke(vault) ?? new RemoteMcpBroker(vault, profile);
        try
        {
            ct.ThrowIfCancellationRequested();
            account = await vault.ConnectAsync(scope, key, ct);
            key = "";
            account = await vault.GrantAsync(scope, account, consumer, [Search, Lookup], ct);
            var result = await broker.ReadAsync(scope, account, consumer, Search,
                JsonSerializer.SerializeToElement(new { limit = 1 }), ct);
            var found = result.GetArrayLength();
            if (found > 1) throw new InvalidOperationException();
            count = found;
            if (count == 1)
            {
                var id = result[0].GetProperty("inheritanceLeadId").GetString();
                if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidOperationException();
                var detail = await broker.ReadAsync(scope, account, consumer, Lookup,
                    JsonSerializer.SerializeToElement(new { id }), ct);
                if (detail.GetProperty("inheritanceLeadId").GetString() != id) throw new InvalidOperationException();
                lookup = true;
            }
            status = "passed";
        }
        catch (RemoteMcpFailureException e)
        { status = "failed"; lookup = false; category = e.Category.ToString(); stage = e.Stage.ToString(); httpStatus = e.HttpStatus; }
        catch (OperationCanceledException) { status = "cancelled"; }
        catch { status = "failed"; lookup = false; category = account is null ? "InputRejected" : "LocalValidationFailure"; }
        finally
        {
            key = "";
            if (account is not null)
            {
                try
                {
                    // Cleanup has its own short budget even when the test was cancelled.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await vault.RevokeAsync(scope, account, cleanup.Token);
                    try
                    {
                        await broker.ReadAsync(scope, account, consumer, Search,
                            JsonSerializer.SerializeToElement(new { limit = 1 }), cleanup.Token);
                    }
                    catch (UnauthorizedAccessException) { revoked = true; }
                }
                catch { status = "cleanup-failed"; }
            }
            repository.Clear();
        }
        if (account is not null && !revoked) status = "cleanup-failed";
        return new(status, count, lookup, revoked, category, stage, httpStatus);
    }
    private sealed class MemoryRepository : IAccountRepository
    {
        private readonly Dictionary<Guid, ProtectedAccount> records = [];
        public Task<ProtectedAccount?> ReadAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); lock (records) return Task.FromResult(records.GetValueOrDefault(id)); }
        public Task<bool> CompareExchangeAsync(Guid id, long? revision, ProtectedAccount next, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (records)
            {
                if (records.GetValueOrDefault(id)?.Revision != revision) return Task.FromResult(false);
                records[id] = next; return Task.FromResult(true);
            }
        }
        public void Clear() { lock (records) records.Clear(); }
    }
}
