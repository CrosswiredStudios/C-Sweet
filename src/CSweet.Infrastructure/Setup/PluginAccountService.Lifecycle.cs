using System.Text.Json;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    private async Task QueueMaintenance(CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        var due=await db.PluginConnections.Where(c=>c.AccountGroupId!=null && c.Status==PluginConnectionStatus.Connected &&
            c.AgentInstallation!.IsEnabled && c.AgentInstallation.RevisionStatus==PluginRevisionStatus.Active &&
            c.NextInventoryCheckAt!=null && c.NextInventoryCheckAt<=now).OrderBy(c=>c.NextInventoryCheckAt).Take(10).ToArrayAsync(ct);
        foreach(var connection in due)
        {
            try
            {
            var (installation,manifest,declaration)=await Context(connection.AgentInstallationId,ct);
            var group=await db.PluginAccountGroups.SingleAsync(x=>x.Id==connection.AccountGroupId,ct);
            if(group.State!=PluginAccountState.Connected) continue;
            connection.NextInventoryCheckAt=now.AddHours(1);
            if(!await db.PluginAccountSetupSessions.AnyAsync(x=>x.ConnectionId==connection.Id && x.IsMaintenance && x.Status=="Importing",ct))
            {
                var profile=await PluginAccountProfileGuard.RequireAsync(db,profiles,installation,declaration,ct);
                var retry=await db.PluginAccountSetupSessions.Where(x=>x.ConnectionId==connection.Id && x.IsMaintenance && x.Status=="ImportFailed" &&
                    x.CreatedAt>now.AddHours(-24) && x.ReplacesAuthorizationRevision==connection.AuthorizationRevision && x.PackageDigest==installation.PackageVersion!.PackageDigest)
                    .OrderByDescending(x=>x.CreatedAt).FirstOrDefaultAsync(ct);
                if(retry!=null && retry.ProfileHash==PluginAccountProfileGuard.Fingerprint(profile)) { retry.Status="Importing"; retry.FailureCode=null; retry.Revision++; }
                else db.PluginAccountSetupSessions.Add(new(){Id=Guid.NewGuid(),PluginInstallationId=installation.Id,AccountGroupId=group.Id,
                    ConnectionId=connection.Id,ApplicationUserId=null,IsMaintenance=true,ReplacesAuthorizationRevision=connection.AuthorizationRevision,
                    PackageDigest=installation.PackageVersion!.PackageDigest!,ProfileHash=PluginAccountProfileGuard.Fingerprint(profile),PolicyVersion=manifest.Accounts!.PolicyVersion,
                    Status="Importing",CreatedAt=now,ExpiresAt=now.AddHours(24)});
            }
            // The connection's existing concurrency tokens fence duplicate scheduling across workers.
            try { await db.SaveChangesAsync(ct); } catch(DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
            }
            catch(UnauthorizedAccessException)
            { connection.Status=PluginConnectionStatus.ReauthorizationRequired; connection.AuthorizationRevision++; await db.SaveChangesAsync(ct); }
        }
        // Failed scans never remove unseen resources. Once the declared refresh window expires,
        // stale inventory loses authority and provider display metadata is removed instead of retained indefinitely.
        foreach(var installation in await db.AgentInstallations.AsNoTracking().Include(x=>x.PackageVersion).Where(x=>x.Scope==PluginInstallationScope.System && x.PackageVersion!.PluginKind==PluginKind.Connector).ToArrayAsync(ct))
        {
            var manifest=JsonSerializer.Deserialize<CSweet.Contracts.Plugins.PluginManifest>(installation.PackageVersion!.ManifestJson,Json);
            if(manifest?.Accounts is not { } account) continue;
            var before=now.AddDays(-account.Lifecycle.MetadataRefreshDays);
            using var erasure=db.BeginProviderDataErasure();
            foreach(var resource in await db.PluginAccountResources.Where(x=>x.PluginInstallationId==installation.Id && x.VerifiedAt<=before && x.DisplayName!="Expired resource").ToArrayAsync(ct))
            { resource.IsAvailable=false; resource.DisplayName="Expired resource"; resource.Handle=null; resource.ExternalResourceId=$"erased-{resource.Id:N}"; resource.Revision++; }
            foreach(var attempt in await db.PluginAccountSetupSessions.Where(x=>x.PluginInstallationId==installation.Id && x.CreatedAt<=before &&
                x.Status!="Importing" && x.Status!="Archived" && x.Status!="Erased").ToArrayAsync(ct))
            { attempt.ObservedResourcesJson="[]"; attempt.SeenPageTokensJson="[]"; attempt.PageToken=null; attempt.VerifiedSubject=null; attempt.VerifiedName=null; attempt.IdentityAuthority=null; attempt.Status="Archived"; attempt.Revision++; }
            var accountIds=await db.PluginAccountGroups.Where(x=>x.PluginInstallationId==installation.Id).Select(x=>x.Id).ToArrayAsync(ct);
            foreach(var copy in await db.PluginAccountDataCopies.Where(x=>accountIds.Contains(x.AccountGroupId) && x.CreatedAt<=before && x.DeletedAt==null &&
                x.Store!="consumer-origin" && !x.Store.StartsWith("external:")).Take(100).ToArrayAsync(ct))
                if(await EraseRecord(copy.Store,copy.RecordId,ct)) { copy.DeletedAt=now; copy.Revision++; }
            await db.SaveChangesAsync(ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
