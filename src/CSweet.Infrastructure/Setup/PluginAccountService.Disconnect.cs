using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    public async Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid userId, Guid installationId, Guid accountId, CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(userId, ct);
        var (installation, manifest, _) = await Context(installationId, ct,true);
        var group = await db.PluginAccountGroups.AsNoTracking().SingleAsync(x => x.Id == accountId && x.PluginInstallationId == installationId, ct);
        var connections = await db.PluginConnections.AsNoTracking().Where(x => x.AgentInstallationId == installationId && x.AccountGroupId == accountId &&
            x.Status != PluginConnectionStatus.Revoked).OrderBy(x => x.Id).ToArrayAsync(ct);
        var grants = await db.PluginAccountBusinessGrants.AsNoTracking().Where(x => x.PluginInstallationId == installationId && x.AccountGroupId == accountId && x.RevokedAt == null).OrderBy(x => x.Id).ToArrayAsync(ct);
        var ids = grants.Select(x => x.Id).ToArray();
        var bindings = await db.AgentCapabilityBindings.AsNoTracking().Where(x => x.PluginAccountBusinessGrantId != null && ids.Contains(x.PluginAccountBusinessGrantId.Value) && x.RevokedAt == null).OrderBy(x => x.Id).ToArrayAsync(ct);
        var copies=await db.PluginAccountDataCopies.AsNoTracking().Where(x=>x.AccountGroupId==accountId && x.DeletedAt==null).OrderBy(x=>x.Id).ToArrayAsync(ct);
        var organizations = grants.Select(g => g.OrganizationId).Concat(copies.Where(x=>x.OrganizationId!=null).Select(x=>x.OrganizationId!.Value)).Distinct().ToArray();
        var consumers = bindings.Select(b => b.RequesterInstallationId).Concat(copies.Where(x=>x.ConsumerInstallationId!=installationId).Select(x=>x.ConsumerInstallationId)).Distinct().ToArray();
        var names = await db.CoreOrganizations.AsNoTracking().Where(x => organizations.Contains(x.Id)).Select(x => x.Name).ToArrayAsync(ct);
        var agents = await db.AgentInstallations.AsNoTracking().Where(x => consumers.Contains(x.Id)).Select(x => x.PackageVersion!.AgentName).ToArrayAsync(ct);
        var hash = ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(new { group.Id, group.Revision, installation.PackageVersion!.PackageDigest,
            connections = connections.Select(x => new { x.Id, x.AuthorizationRevision, x.Status }), grants = grants.Select(x => new { x.Id, x.Revision }),
            bindings = bindings.Select(x => new { x.Id, x.ApprovedAt, x.PluginAccountResourceId, x.PluginAccountConnectionId }),copies=copies.Select(x=>new{x.Id,x.Revision}) }));
        return new(group.Revision, hash, names, agents, manifest.Accounts!.Lifecycle.RevocationScope == "subject-project") {RecordedCopies=copies.Length};
    }

    public async Task DisconnectAsync(Guid userId, Guid installationId, Guid accountId, DisconnectPluginAccountRequest request, CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(userId,ct);
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction==null ? await db.Database.BeginTransactionAsync(ct) : null;
        if(db.Database.IsNpgsql()) _=await db.PluginAccountGroups.FromSqlInterpolated($"SELECT * FROM \"PluginAccountGroups\" WHERE \"Id\" = {accountId} AND \"PluginInstallationId\" = {installationId} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        var impact = await GetDisconnectImpactAsync(userId, installationId, accountId, ct);
        if (request.AccountRevision != impact.AccountRevision || request.ReviewHash != impact.ReviewHash)
            throw new InvalidOperationException("Account access changed. Review the current impact before disconnecting.");
        using var controlScope=db.BeginProviderDataErasure();
        var (_, manifest, _) = await Context(installationId, ct,true);
        var group = await db.PluginAccountGroups.SingleAsync(x => x.Id == accountId && x.PluginInstallationId == installationId, ct);
        if(group.State==PluginAccountState.Disconnected) throw new InvalidOperationException("This account is already disconnected.");
        if(group.State==PluginAccountState.Disconnecting) return;
        group.State = PluginAccountState.Disconnecting; group.Revision++; group.UpdatedAt = DateTimeOffset.UtcNow;
        if(!await db.PluginAccountDeletions.AnyAsync(x=>x.AccountGroupId==accountId,ct))
            db.PluginAccountDeletions.Add(new(){AccountGroupId=accountId,PluginInstallationId=installationId,RequestedAt=DateTimeOffset.UtcNow,
                TargetAt=DateTimeOffset.UtcNow.AddHours(manifest.Accounts!.Lifecycle.DeletionTargetHours),Deadline=DateTimeOffset.UtcNow.AddDays(manifest.Accounts.Lifecycle.DeletionMaximumDays)});
        await db.TrackCopy(accountId,null,installationId,"external:backups",accountId,ct);
        var grants = await db.PluginAccountBusinessGrants.Where(x => x.PluginInstallationId == installationId && x.AccountGroupId == accountId && x.RevokedAt == null).ToArrayAsync(ct);
        foreach (var grant in grants) { await InvalidateGrant(grant, ct); grant.RevokedAt = DateTimeOffset.UtcNow; grant.Revision++; }
        var connections = await db.PluginConnections.Where(x => x.AgentInstallationId == installationId && x.AccountGroupId == accountId && x.Status != PluginConnectionStatus.Revoked).ToArrayAsync(ct);
        foreach (var connection in connections)
        {
            var key = await PluginOAuthCredentialKeys.ResolveAsync(db, installationId, connection.Id, ct);
            var managedContext = await secrets.GetAsync(installationId, ManagedContextKey(connection.Id), ct);
            if (managedContext is not null) await db.TrackCopy(accountId, null, installationId, "external:managed-service-backups", accountId, ct);
            var neverAuthorized=managedContext is null && connection.Status==PluginConnectionStatus.Pending && await secrets.GetAsync(installationId,key,ct)==null;
            connection.Status = PluginConnectionStatus.Revoked; connection.AuthorizationRevision++; connection.UpdatedAt = DateTimeOffset.UtcNow;
            if (!neverAuthorized && !await db.PluginAccountRevocationJobs.AnyAsync(x => x.ConnectionId == connection.Id, ct))
                db.PluginAccountRevocationJobs.Add(new() { Id = Guid.NewGuid(), PluginInstallationId = installationId, AccountGroupId = accountId, ConnectionId = connection.Id,
                    ProfileId = connection.ProviderProfile, ProfileHash = connection.CredentialProfileHash ?? "", CredentialKey = key,
                    Deadline = DateTimeOffset.UtcNow.AddDays(manifest.Accounts!.Lifecycle.DeletionMaximumDays), NextAttempt = DateTimeOffset.UtcNow });
        }
        foreach (var session in await db.PluginAccountSetupSessions.Where(x => x.PluginInstallationId == installationId && x.AccountGroupId == accountId &&
            (x.Status == "AwaitingConsent" || x.Status == "Importing" || x.Status == "ImportFailed")).ToArrayAsync(ct))
        { session.Status = "Cancelled"; session.Revision++; }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin-account.disconnect.queued", nameof(PluginAccountGroup), group.Id,
            "An administrator reviewed the current impact and stopped account access. Provider revocation and authorized-data deletion require durable confirmation.", cancellationToken: ct);
        if (tx is not null) await tx.CommitAsync(ct);
        // Revocation and data deletion are durable follow-up work. No claim that sent provider effects can be undone.
    }

    private async Task ProcessRevocations(CancellationToken ct)
    {
        var jobs = await db.PluginAccountRevocationJobs.Where(x => x.Status == "Pending" && x.NextAttempt <= DateTimeOffset.UtcNow).Take(20).ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Attempts++; job.Revision++; job.NextAttempt = DateTimeOffset.UtcNow.AddMinutes(2);
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { continue; }
            var managedJson = await secrets.GetAsync(job.PluginInstallationId, ManagedContextKey(job.ConnectionId), ct);
            if (managedJson is not null)
            {
                try
                {
                    var remote = JsonSerializer.Deserialize<ManagedAccountAttempt>(managedJson, Json)!;
                    // Stored protected context selects the old backend, regardless of the mode
                    // configured for future connections. Never send its cleanup to local OAuth.
                    if (remote is null || !await RevokeManaged(job.PluginInstallationId, job.ConnectionId, remote, ct)) continue;
                    await secrets.RemoveAsync(job.PluginInstallationId, ManagedContextKey(job.ConnectionId), ct);
                    job.Status = "RevokedDataDeletionPending"; job.Revision++; await db.SaveChangesAsync(ct);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or
                    UnauthorizedAccessException or System.Security.Cryptography.CryptographicException ||
                    error is OperationCanceledException && !ct.IsCancellationRequested) { }
                continue;
            }
            var source = await secrets.GetAsync(job.PluginInstallationId, job.CredentialKey, ct);
            var quarantine = $"oauth.account-revocation.{job.Id:N}";
            var token = await secrets.GetAsync(job.PluginInstallationId, quarantine, ct);
            if (token is null && source is not null) { await secrets.SetAsync(job.PluginInstallationId, quarantine, source, ct); token = source; }
            await secrets.RemoveAsync(job.PluginInstallationId, job.CredentialKey, ct);
            // Losing local credentials is not proof that the provider revoked the grant.
            var confirmed = false;
            var profile = await profiles.ResolveAsync(job.ProfileId, ct);
            if (token is not null && profile is not null && PluginAccountProfileGuard.Fingerprint(profile) == job.ProfileHash &&
                !string.IsNullOrEmpty(profile.RevocationEndpoint))
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<PluginAccountTokenEnvelope>(token, Json);
                    if(envelope!=null)
                    {
                        using var response = await http.CreateClient(nameof(PluginSetupService)).PostAsync(profile.RevocationEndpoint,
                            new FormUrlEncodedContent(new Dictionary<string,string> { ["token"] = envelope.RefreshToken ?? envelope.AccessToken }), ct);
                        confirmed = response.IsSuccessStatusCode;
                    }
                }
                catch(Exception error) when(error is HttpRequestException or JsonException || error is OperationCanceledException && !ct.IsCancellationRequested)
                { /* Retry durably. A provider outage must not prevent independent local data cleanup. */ }
            }
            if (confirmed || DateTimeOffset.UtcNow >= job.Deadline)
            {
                await secrets.RemoveAsync(job.PluginInstallationId, quarantine, ct);
                job.Status = confirmed ? "RevokedDataDeletionPending" : "UnconfirmedDataDeletionPending";
                job.Revision++; await db.SaveChangesAsync(ct);
                // Keep the account visibly Disconnecting until provenance-aware deletion of every
                // authorized copy (including conversations/processors/backups) is independently confirmed.
            }
        }
    }
}
