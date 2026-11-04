using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Explicit, conservative managed-account adoption. Original consent/history stay immutable;
/// only current native ownership and the same reviewed grants move. No provider credentials leave the service.</summary>
public sealed class PluginAccountUpgradeService(CSweetDbContext db,IPluginSecretStore secrets,IPluginSecretReferenceRebinder references,
    IManagedPluginConnectionGateway managed,IDataProtectionProvider protection,IAuditEventWriter audit)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly IDataProtector reviewProtection=protection.CreateProtector("CSweet.PluginAccountCompatibleUpgrade.Review.v1");
    private PluginAccountAccessService Access=>new(db);
    private sealed record Review(Guid User,Guid Target,DateTimeOffset ExpiresAt,string LocalHash,string Origin,Guid RemoteConnection,
        ConnectionAdoptionRequest Request,string ServiceReview,string Compatible)
    {public override string ToString()=>"Compatible upgrade review [redacted]";}
    private sealed record Snapshot(AgentInstallation Source,AgentInstallation Target,PluginAccountGroup Account,PluginConnection Connection,
        PluginAccountSetupSession Original,PluginAccountResource[] Resources,PluginAccountBusinessGrant[] Grants,string[] ScopeSets,
        ManagedAccountAttempt Context,string Hash,string Compatible,string SecretKey);

    public async Task<PluginAccountUpgradeReview> ReviewAsync(Guid user,Guid targetId,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(user,ct);
        if(await db.PluginAccountUpgrades.AnyAsync(x=>x.TargetInstallationId==targetId && x.Status=="Pending",ct))
            throw Changed("Resume or cancel the existing upgrade intent first.");
        var snapshot=await Require(user,targetId,null,ct);
        var remote=await managed.Snapshot(snapshot.Context.RemoteConnectionId!.Value,ct,snapshot.Context.ServiceOrigin);
        if(remote.ConnectionId!=snapshot.Context.RemoteConnectionId || remote.Status!="Ready" || remote.Revision<1 ||
            !remote.GrantedScopeSets.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.ScopeSets))throw Changed("The managed connection is not ready for adoption.");
        var request=new ConnectionAdoptionRequest(Guid.NewGuid(),snapshot.Source.Id,targetId,snapshot.Source.PackageVersion!.PackageDigest!,
            snapshot.Target.PackageVersion!.PackageDigest!,remote.Revision,snapshot.Connection.CredentialProfileHash!,user.ToString("D"),snapshot.Context.RemoteSessionId);
        var preview=await managed.PreviewAdoption(remote.ConnectionId,request,snapshot.Context.ServiceOrigin!,ct);
        if(preview.RequestHash!=Hash(request) || preview.CompatibilityHash!=snapshot.Compatible || preview.ConnectionRevision!=remote.Revision ||
            !preview.GrantedScopeSets.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.ScopeSets))throw Changed("The service compatibility review differs.");
        var expires=DateTimeOffset.UtcNow.AddMinutes(10);
        var token=reviewProtection.Protect(JsonSerializer.Serialize(new Review(user,targetId,expires,snapshot.Hash,snapshot.Context.ServiceOrigin!,
            remote.ConnectionId,request,preview.ReviewHash,preview.CompatibilityHash),Json));
        return new(snapshot.Source.Id,targetId,snapshot.Source.PackageVersion.Version,snapshot.Target.PackageVersion.Version,
            request.SourcePackageDigest,request.TargetPackageDigest,snapshot.Context.ServiceOrigin!,snapshot.Resources.Count(x=>x.IsAvailable),
            snapshot.Grants.Length,snapshot.ScopeSets,expires,token);
    }

    public async Task<PluginAccountUpgradeProgress?> StatusAsync(Guid user,Guid target,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(user,ct);
        var row=await db.PluginAccountUpgrades.AsNoTracking().Where(x=>x.TargetInstallationId==target)
            .OrderByDescending(x=>x.CreatedAt).FirstOrDefaultAsync(ct);
        return row is null?null:Progress(row);
    }

    public async Task<PluginAccountUpgradeProgress> ApproveAsync(Guid user,Guid target,ApprovePluginAccountUpgrade approval,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(user,ct);Review review;
        if(string.IsNullOrEmpty(approval.ReviewToken) || approval.ReviewToken.Length>16384)throw Changed("A bounded compatible upgrade review is required.");
        try{review=JsonSerializer.Deserialize<Review>(reviewProtection.Unprotect(approval.ReviewToken),Json)??throw new CryptographicException();}
        catch(Exception e) when(e is CryptographicException or JsonException or ArgumentException)
        {throw Changed("Review this exact compatible upgrade again.");}
        if(review.User!=user || review.Target!=target || review.ExpiresAt<=DateTimeOffset.UtcNow || review.Request.ActorId!=user.ToString("D"))
            throw Changed("The compatible upgrade review expired or belongs to another account.");
        var existing=await db.PluginAccountUpgrades.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==review.Request.AdoptionId,ct);
        if(existing is not null)return await ResumeAsync(user,target,existing.Id,ct);
        await using(var tx=await Transaction(ct))
        {
            await LockSource(review.Request.SourceInstallationId,ct);
            var snapshot=await Require(user,target,null,ct);
            if(snapshot.Hash!=review.LocalHash || snapshot.Compatible!=review.Compatible || snapshot.Context.ServiceOrigin!=review.Origin ||
                snapshot.Context.RemoteConnectionId!=review.RemoteConnection)throw Changed("Account access changed after review.");
            db.PluginAccountUpgrades.Add(new(){Id=review.Request.AdoptionId,SourceInstallationId=snapshot.Source.Id,TargetInstallationId=target,
                AccountGroupId=snapshot.Account.Id,ConnectionId=snapshot.Connection.Id,OriginalSetupSessionId=snapshot.Original.Id,ApplicationUserId=user,
                SourceSnapshotHash=snapshot.Hash,ServiceOrigin=review.Origin,RemoteConnectionId=review.RemoteConnection,
                ServiceRequestJson=JsonSerializer.Serialize(review.Request,Json),ServiceReviewHash=review.ServiceReview,CompatibilityHash=review.Compatible,
                CreatedAt=DateTimeOffset.UtcNow});
            var source=await db.AgentInstallations.SingleAsync(x=>x.Id==snapshot.Source.Id,ct);
            source.IsEnabled=false;source.UpdatedAt=DateTimeOffset.UtcNow; // Durable fence before any cross-database adoption.
            await audit.WriteAsync("plugin-account.upgrade.prepared",nameof(PluginAccountUpgrade),review.Request.AdoptionId,
                "An administrator approved an exact compatible package and unchanged selected-resource business grants; source execution is fenced pending its service receipt.",cancellationToken:ct);
            await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);
        }
        return await ResumeAsync(user,target,review.Request.AdoptionId,ct);
    }

    public async Task<PluginAccountUpgradeProgress> ResumeAsync(Guid user,Guid target,Guid id,CancellationToken ct)
    {
        var row=await Intent(user,target,id,ct);if(row.Status!="Pending")return Progress(row);
        var request=JsonSerializer.Deserialize<ConnectionAdoptionRequest>(row.ServiceRequestJson,Json)!;
        try
        {
            var status=await managed.Adoption(id,row.ServiceOrigin,ct); // Reconcile an uncertain acknowledgment first.
            ConnectionAdoptionReceipt receipt;
            if(status.Found)receipt=status.Receipt??throw Changed("The adoption receipt is incomplete.");
            else
            {
                var current=await Require(user,target,row,ct);
                if(current.Hash!=row.SourceSnapshotHash)throw Changed("Account access changed while adoption was pending.");
                receipt=await managed.Adopt(row.RemoteConnectionId,new(request,row.ServiceReviewHash),row.ServiceOrigin,ct);
            }
            if(receipt.Outcome=="Cancelled")return await FinishCancelled(row,receipt,ct);
            RequireReceipt(row,request,receipt);
            await using var tx=await Transaction(ct);await LockSource(row.SourceInstallationId,ct);
            db.ChangeTracker.Clear();row=await Intent(user,target,id,ct);if(row.Status!="Pending")return Progress(row);
            var snapshot=await Require(user,target,row,ct);
            if(snapshot.Hash!=row.SourceSnapshotHash || snapshot.Compatible!=row.CompatibilityHash)throw Changed("Account access changed while adoption was pending.");
            // The service receipt never grants Core access by itself. Recheck the paired origin/profile
            // and exact local resource/consent/grant inventory before the atomic native move.
            var available=await managed.Availability(request.TargetPackageDigest,ct);
            if(available.BindingHash!=request.ExpectedProfileBinding || available.ProfileId!=snapshot.Connection.ProviderProfile ||
                await managed.BoundServiceOrigin(ct)!=row.ServiceOrigin)throw Changed("The service profile or paired origin changed.");
            // A fresh receipt status prevents a known intervening revoke/another adoption being accepted.
            var fresh=await managed.Adoption(id,row.ServiceOrigin,ct);RequireReceipt(row,request,fresh.Receipt??throw Changed("The adoption receipt is unavailable."));
            await references.StageAsync(snapshot.Source.Id,target,[snapshot.SecretKey],ct);
            var account=await db.PluginAccountGroups.SingleAsync(x=>x.Id==snapshot.Account.Id,ct);
            account.PluginInstallationId=target;account.Revision++;account.UpdatedAt=DateTimeOffset.UtcNow;
            var connection=await db.PluginConnections.SingleAsync(x=>x.Id==snapshot.Connection.Id,ct);
            connection.AgentInstallationId=target;connection.AuthorizationRevision++;connection.UpdatedAt=DateTimeOffset.UtcNow;
            foreach(var resource in await db.PluginAccountResources.Where(x=>x.AccountGroupId==account.Id && x.PluginInstallationId==snapshot.Source.Id).ToArrayAsync(ct))
            {resource.PluginInstallationId=target;resource.Revision++;resource.UpdatedAt=DateTimeOffset.UtcNow;}
            foreach(var grant in await db.PluginAccountBusinessGrants.Where(x=>x.AccountGroupId==account.Id && x.PluginInstallationId==snapshot.Source.Id && x.RevokedAt==null).ToArrayAsync(ct))
            {grant.PluginInstallationId=target;grant.PackageDigest=request.TargetPackageDigest;grant.Revision++;grant.UpdatedAt=DateTimeOffset.UtcNow;}
            var source=await db.AgentInstallations.Include(x=>x.Grant).SingleAsync(x=>x.Id==snapshot.Source.Id,ct);
            var destination=await db.AgentInstallations.Include(x=>x.Grant).SingleAsync(x=>x.Id==target,ct);
            destination.Grant!.ProvidedCapabilitiesJson=source.Grant!.ProvidedCapabilitiesJson;
            destination.Grant.RequiredCapabilitiesJson=source.Grant.RequiredCapabilitiesJson;
            destination.Grant.EventSubscriptionsJson=source.Grant.EventSubscriptionsJson;
            destination.Grant.GrantRevision++;destination.Grant.ApprovedAt=DateTimeOffset.UtcNow;
            source.RevisionStatus=PluginRevisionStatus.Retired;source.IsEnabled=false;source.UpdatedAt=DateTimeOffset.UtcNow;
            destination.RevisionStatus=PluginRevisionStatus.Active;destination.IsEnabled=true;destination.UpdatedAt=DateTimeOffset.UtcNow;
            destination.SetupState=PluginSetupState.Ready;
            row.Status="Completed";row.Revision++;row.CompletedAt=DateTimeOffset.UtcNow;
            await audit.WriteAsync("plugin-account.upgrade.completed",nameof(PluginAccountUpgrade),id,
                "A verified compatible service receipt and current native review transferred the same consent, resource identities and selected business permissions. Original consent and cleanup history remain; no agent bindings, policies, new scopes or deletion receipts were created.",cancellationToken:ct);
            await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);return Progress(row);
        }
        catch(Exception e) when(e is HttpRequestException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or DbUpdateException or JsonException or CryptographicException ||
            e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();return Progress(await Intent(user,target,id,ct));
            // No automatic second POST, no rollback claim and no re-enabling an uncertain source.
        }
    }

    public async Task<PluginAccountUpgradeProgress> CancelAsync(Guid user,Guid target,Guid id,CancellationToken ct)
    {
        var row=await Intent(user,target,id,ct);if(row.Status!="Pending")return Progress(row);
        var request=JsonSerializer.Deserialize<ConnectionAdoptionRequest>(row.ServiceRequestJson,Json)!;
        var receipt=await managed.CancelAdoption(row.RemoteConnectionId,request,row.ServiceOrigin,ct);
        if(receipt.Outcome=="Adopted")return await ResumeAsync(user,target,id,ct); // Cannot pretend an accepted adoption was rolled back.
        return await FinishCancelled(row,receipt,ct);
    }

    private async Task<PluginAccountUpgradeProgress> FinishCancelled(PluginAccountUpgrade row,ConnectionAdoptionReceipt receipt,CancellationToken ct)
    {
        var request=JsonSerializer.Deserialize<ConnectionAdoptionRequest>(row.ServiceRequestJson,Json)!;
        if(receipt.AdoptionId!=row.Id || receipt.ConnectionId!=row.RemoteConnectionId || receipt.RequestHash!=Hash(request) ||
            receipt.Outcome!="Cancelled")throw Changed("The cancellation receipt changed.");
        await using var tx=await Transaction(ct);await LockSource(row.SourceInstallationId,ct);
        var restore=false;
        try{var snapshot=await Require(row.ApplicationUserId,row.TargetInstallationId,row,ct);
            var current=await managed.Snapshot(row.RemoteConnectionId,ct,row.ServiceOrigin);
            restore=snapshot.Hash==row.SourceSnapshotHash && receipt.CurrentInstallationId==request.SourceInstallationId &&
                receipt.CurrentPackageDigest==request.SourcePackageDigest && receipt.CurrentStatus=="Ready" && current.Status=="Ready" &&
                current.ConnectionId==row.RemoteConnectionId && await managed.BoundServiceOrigin(ct)==row.ServiceOrigin &&
                (await managed.Availability(request.SourcePackageDigest,ct)).BindingHash==request.ExpectedProfileBinding;
        }catch(Exception e)when(e is InvalidOperationException or UnauthorizedAccessException or HttpRequestException){restore=false;}
        var saved=await db.PluginAccountUpgrades.SingleAsync(x=>x.Id==row.Id,ct);
        if(saved.Status!="Pending")return Progress(saved);
        if(restore)(await db.AgentInstallations.SingleAsync(x=>x.Id==row.SourceInstallationId,ct)).IsEnabled=true;
        saved.Status=restore?"Cancelled":"CancelledReviewRequired";saved.Revision++;saved.CompletedAt=DateTimeOffset.UtcNow;
        await audit.WriteAsync("plugin-account.upgrade.cancelled",nameof(PluginAccountUpgrade),saved.Id,
            "A service tombstone prevents late adoption. Existing access is resumed only when its exact reviewed authority is unchanged; otherwise it stays fenced for review.",cancellationToken:ct);
        await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);return Progress(saved);
    }

    private async Task<Snapshot> Require(Guid user,Guid targetId,PluginAccountUpgrade? intent,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(user,ct);
        var target=await db.AgentInstallations.AsNoTracking().Include(x=>x.PackageVersion).Include(x=>x.Grant).Include(x=>x.Schedule).SingleOrDefaultAsync(x=>x.Id==targetId,ct)??throw Changed();
        if(target.SupersedesInstallationId is not{} sourceId)throw Changed();
        var source=await db.AgentInstallations.AsNoTracking().Include(x=>x.PackageVersion).Include(x=>x.Grant).Include(x=>x.Schedule).SingleAsync(x=>x.Id==sourceId,ct);
        if(source.Scope!=PluginInstallationScope.System || target.Scope!=source.Scope || source.BusinessId!=target.BusinessId ||
            source.RevisionStatus!=PluginRevisionStatus.Active || source.IsEnabled!=(intent is null) || target.IsEnabled ||
            target.RevisionStatus!=PluginRevisionStatus.Staged || source.PackageVersion!.PluginKind!=PluginKind.Connector || target.PackageVersion!.PluginKind!=PluginKind.Connector ||
            source.PackageVersion.Status!=AgentPackageVersionStatus.Built || target.PackageVersion.Status!=AgentPackageVersionStatus.Built ||
            source.PackageVersion.PackageSourceId!=target.PackageVersion.PackageSourceId || source.PackageVersion.PackageDigest==target.PackageVersion.PackageDigest ||
            target.InstallationKey!=(source.InstallationKey==Guid.Empty?source.Id:source.InstallationKey) || source.Grant is null || target.Grant is null ||
            source.Schedule is not null || target.Schedule is not null)throw Changed();
        if(await db.PluginAccountUpgrades.AnyAsync(x=>x.Status=="Pending" && (x.SourceInstallationId==sourceId || x.TargetInstallationId==targetId) &&
            (intent==null || x.Id!=intent.Id),ct))throw Changed("Another adoption is pending.");
        if(await db.PluginAccountAdministrations.AnyAsync(x=>(x.PluginInstallationId==sourceId || x.PluginInstallationId==targetId) && x.RemovalRequestedAt!=null,ct) ||
            await db.PluginAccountOperators.AnyAsync(x=>x.PluginInstallationId==sourceId && x.RevokedAt==null,ct) ||
            await db.AgentCapabilityBindings.AnyAsync(x=>x.ProviderInstallationId==sourceId && x.RevokedAt==null,ct) ||
            await db.PluginAccountGroups.AnyAsync(x=>x.PluginInstallationId==targetId,ct) ||
            await db.PluginConnections.AnyAsync(x=>x.AgentInstallationId==targetId,ct) ||
            await db.PluginSecrets.AnyAsync(x=>x.PluginInstallationId==targetId,ct) ||
            await db.PluginAccountRevocationJobs.AnyAsync(x=>x.PluginInstallationId==sourceId && x.Status!="RevokedDataDeletionPending",ct) ||
            await db.PluginAccountDeletions.AnyAsync(x=>x.PluginInstallationId==sourceId && x.CompletedAt==null,ct) ||
            await db.PluginAccountSetupSessions.AnyAsync(x=>x.PluginInstallationId==sourceId && (x.Status=="AwaitingConsent" || x.Status=="Importing" || x.RetiredCredentialKey!=null || x.LeaseOwner!=null),ct) ||
            await db.ConnectorExecutions.AnyAsync(x=>x.ConnectorInstallationId==sourceId && x.Status!="Completed" && x.Status!="Rejected" && x.Status!="Cancelled" && x.Status!="Expired" && x.Status!="Erased",ct) ||
            await db.PluginOperationalStates.AnyAsync(x=>x.AgentInstallationId==sourceId && (x.Kind==ConnectorMediaTransferService.ActiveKind || x.Kind==ConnectorMediaTransferService.FinishedKind) && x.AvailableAt!=null,ct))
            throw Changed("This revision has active or unresolved work; compatible adoption is unavailable.");
        var groups=await db.PluginAccountGroups.AsNoTracking().Where(x=>x.PluginInstallationId==sourceId && x.State!=PluginAccountState.Disconnected).ToArrayAsync(ct);
        var connections=await db.PluginConnections.AsNoTracking().Where(x=>x.AgentInstallationId==sourceId && x.Status!=PluginConnectionStatus.Revoked).ToArrayAsync(ct);
        if(connections.Length!=1)throw Changed("Compatible adoption currently requires one connected provider-verified account and authorization.");
        var connection=connections[0];
        var group=groups.SingleOrDefault(x=>x.Id==connection.AccountGroupId)??throw Changed("The authorization account is unavailable.");
        if(group.State!=PluginAccountState.Connected || group.IdentityKind!=PluginAccountIdentityKind.ProviderSubject || string.IsNullOrWhiteSpace(group.VerifiedSubject) ||
            connection.Status!=PluginConnectionStatus.Connected || connection.AccountGroupId!=group.Id || connection.ExternalAccountId!=group.VerifiedSubject ||
            connection.ProviderProfile!=group.ProviderProfile || connection.RefreshLeaseOwner!=null || connection.RefreshLeaseUntil!=null || connection.ActiveCredentialAttemptId!=null)
            throw Changed("The current account is not a compatible managed authorization.");
        var historicalHash=await HistoricalAccounts(sourceId,groups.Where(x=>x.Id!=group.Id).ToArray(),connection.ProviderProfile,ct);
        var original=await ManagedAccountProvenance.Original(db,sourceId,connection.Id,ct);
        if(original.ApplicationUserId!=user || original.Status is not ("Ready" or "Archived"))throw Changed("The original consenting administrator must review this upgrade.");
        var key=$"managed.context.{connection.Id:N}";
        var reference=await db.PluginSecrets.AsNoTracking().Where(x=>x.PluginInstallationId==sourceId).Select(x=>new{x.Id,x.Key,x.UpdatedAt}).ToArrayAsync(ct);
        if(reference.Length!=1 || reference[0].Key!=key)throw Changed("The protected reference inventory is not eligible for adoption.");
        var context=JsonSerializer.Deserialize<ManagedAccountAttempt>(await secrets.GetAsync(sourceId,key,ct)??throw Changed(),Json)??throw Changed();
        if(context.RemoteSessionId==Guid.Empty || context.RemoteConnectionId is null || context.RemoteConnectionId==Guid.Empty || context.ServiceOrigin is null || !managed.UsesManaged(connection.ProviderProfile) ||
            await managed.BoundServiceOrigin(ct)!=context.ServiceOrigin)throw Changed("The paired managed service changed.");
        var availability=await managed.Availability(target.PackageVersion.PackageDigest!,ct);
        if(availability.ProfileId!=connection.ProviderProfile || availability.BindingHash!=connection.CredentialProfileHash)throw Changed("The target profile must have separate matching service approval.");
        var before=JsonSerializer.Deserialize<PluginManifest>(source.PackageVersion.ManifestJson,Json)!;
        var after=JsonSerializer.Deserialize<PluginManifest>(target.PackageVersion.ManifestJson,Json)!;
        PluginManifestReader.ValidateConnectorContracts(before);PluginManifestReader.ValidateConnectorContracts(after);
        var declaration=before.Connections.Single(x=>x.Id==before.Accounts!.Connection);
        if(connection.DeclarationId!=declaration.Id || declaration.ProviderProfile!=connection.ProviderProfile || before.Accounts!.Identity is not{} identity ||
            group.IdentityAuthority!=before.ProviderOperations.Single(x=>x.Capability==identity.Capability).Http!.Endpoint)throw Changed("The verified identity contract changed.");
        var scopes=JsonSerializer.Deserialize<string[]>(connection.GrantedScopesJson)!;
        var sets=declaration.ScopeSets.Where(x=>!x.Scopes.Except(scopes,StringComparer.Ordinal).Any()).Select(x=>x.Id).ToArray();
        if(!declaration.ScopeSets.Where(x=>sets.Contains(x.Id)).SelectMany(x=>x.Scopes).ToHashSet(StringComparer.Ordinal).SetEquals(scopes))throw Changed();
        using var oldJson=JsonDocument.Parse(source.PackageVersion.ManifestJson);using var newJson=JsonDocument.Parse(target.PackageVersion.ManifestJson);
        var compatible=CompatibleConnectionContract.Require(oldJson.RootElement,newJson.RootElement,sets);
        var resources=await db.PluginAccountResources.AsNoTracking().Where(x=>x.AccountGroupId==group.Id).OrderBy(x=>x.Id).ToArrayAsync(ct);
        var proofs=await db.PluginAccountResourceAuthorizations.AsNoTracking().Where(x=>x.ConnectionId==connection.Id).OrderBy(x=>x.ResourceId).ToArrayAsync(ct);
        var grants=await db.PluginAccountBusinessGrants.AsNoTracking().Where(x=>x.AccountGroupId==group.Id && x.RevokedAt==null).OrderBy(x=>x.Id).ToArrayAsync(ct);
        foreach(var grant in grants)
        {
            var selected=JsonSerializer.Deserialize<Guid[]>(grant.SelectedResourceIdsJson)!;
            var capabilities=JsonSerializer.Deserialize<string[]>(grant.CapabilitiesJson)!;
            if(grant.PluginInstallationId!=sourceId || grant.PackageDigest!=source.PackageVersion.PackageDigest || grant.PolicyVersion!=before.Accounts!.PolicyVersion ||
                grant.Scope!=PluginAccountGrantScope.SelectedResources || selected.Length==0 || selected.Distinct().Count()!=selected.Length ||
                selected.Any(id=>!resources.Any(r=>r.Id==id && r.PluginInstallationId==sourceId && r.IsAvailable) || !proofs.Any(p=>p.ResourceId==id && p.IsAvailable)) ||
                capabilities.Any(cap=>!before.ProviderOperations.Any(op=>op.Capability==cap && op.Http is{} http && http.ScopeSets.All(s=>sets.Contains(s,StringComparer.Ordinal)))))throw Changed("The exact selected-resource grants are not compatible.");
        }
        var sourceProvided=JsonSerializer.Deserialize<string[]>(source.Grant.ProvidedCapabilitiesJson)!;
        var sourceRequired=JsonSerializer.Deserialize<string[]>(source.Grant.RequiredCapabilitiesJson)!;
        if(sourceProvided.Except(after.Provides.Select(x=>x.Name),StringComparer.Ordinal).Any() || sourceRequired.Except(after.Requires.Select(x=>x.Name),StringComparer.Ordinal).Any() ||
            JsonSerializer.Deserialize<string[]>(source.Grant.NetworkAccessJson)!.Length!=0 ||
            JsonSerializer.Deserialize<string[]>(target.Grant.ProvidedCapabilitiesJson)!.Length!=0 || JsonSerializer.Deserialize<string[]>(target.Grant.RequiredCapabilitiesJson)!.Length!=0 ||
            JsonSerializer.Deserialize<string[]>(target.Grant.NetworkAccessJson)!.Length!=0)throw Changed();
        if(JsonSerializer.Deserialize<string[]>(source.Grant.EventSubscriptionsJson)!.Except(after.Events.Subscribes,StringComparer.Ordinal).Any() ||
            JsonSerializer.Deserialize<string[]>(target.Grant.EventSubscriptionsJson)!.Length!=0)throw Changed();
        var configurations=await db.AgentInstallationConfigurations.AsNoTracking().Where(x=>x.AgentInstallationId==sourceId || x.AgentInstallationId==targetId)
            .OrderBy(x=>x.AgentInstallationId).Select(x=>new{x.AgentInstallationId,x.SchemaVersion,x.SettingsJson}).ToArrayAsync(ct);
        var sourceSettings=configurations.SingleOrDefault(x=>x.AgentInstallationId==sourceId);
        var targetSettings=configurations.SingleOrDefault(x=>x.AgentInstallationId==targetId);
        if((sourceSettings?.SettingsJson??"{}")!=(targetSettings?.SettingsJson??"{}") || (sourceSettings?.SchemaVersion??"1")!=(targetSettings?.SchemaVersion??"1"))throw Changed("Configuration changed between revisions.");
        var administrations=await db.PluginAccountAdministrations.AsNoTracking().Where(x=>x.PluginInstallationId==sourceId || x.PluginInstallationId==targetId)
            .OrderBy(x=>x.PluginInstallationId).Select(x=>new{x.PluginInstallationId,x.Revision,x.RemovalRequestedAt}).ToArrayAsync(ct);
        var hash=Hash(new{source.Id,source.InstallationKey,SourcePackage=source.PackageVersionId,Target=target.Id,TargetPackage=target.PackageVersionId,
            SourceDigest=source.PackageVersion.PackageDigest,TargetDigest=target.PackageVersion.PackageDigest,source.Grant.GrantRevision,
            source.Grant.ProvidedCapabilitiesJson,source.Grant.RequiredCapabilitiesJson,source.Grant.EventSubscriptionsJson,
            TargetGrant=target.Grant.GrantRevision,Account=group.Id,AccountRevision=group.Revision,group.VerifiedSubject,group.IdentityAuthority,
            Connection=connection.Id,connection.AuthorizationRevision,connection.CredentialProfileHash,connection.GrantedScopesJson,resources,proofs,grants,reference,administrations,
            Origin=context.ServiceOrigin,Remote=context.RemoteConnectionId,ConsentSession=context.RemoteSessionId,Original=original.Id,configurations,compatible});
        // Preserve the pre-existing hash for installations without historical placeholders,
        // including durable intents prepared by the prior version. Any excluded history is
        // separately bound to this review and rechecked before intent/finalization/recovery.
        if(historicalHash is not null)hash=Hash(new{Current=hash,Historical=historicalHash});
        return new(source,target,group,connection,original,resources,grants,sets,context,hash,compatible,key);
    }

    private async Task<string?> HistoricalAccounts(Guid source,PluginAccountGroup[] groups,string profile,CancellationToken ct)
    {
        if(groups.Length==0)return null;
        var ids=groups.Select(x=>x.Id).ToArray();
        var connections=await db.PluginConnections.AsNoTracking().Where(x=>x.AccountGroupId!=null && ids.Contains(x.AccountGroupId.Value)).OrderBy(x=>x.Id).ToArrayAsync(ct);
        var connectionIds=connections.Select(x=>x.Id).ToArray();
        var attempts=await db.PluginAccountSetupSessions.AsNoTracking().Where(x=>ids.Contains(x.AccountGroupId)).OrderBy(x=>x.Id).ToArrayAsync(ct);
        var now=DateTimeOffset.UtcNow;
        // These rows are retained on the old installation, not transferred or erased.
        // Revoked is an execution fence, not a claim that every historical copy was erased.
        if(groups.Any(x=>x.PluginInstallationId!=source || x.State!=PluginAccountState.ConnectionRequired ||
                x.IdentityKind!=PluginAccountIdentityKind.Administrative || x.VerifiedSubject is not null ||
                x.IdentityAuthority is not null || x.ProviderProfile!=profile) ||
            groups.Any(g=>!connections.Any(c=>c.AccountGroupId==g.Id) || !attempts.Any(a=>a.AccountGroupId==g.Id)) ||
            connections.Any(x=>x.AgentInstallationId!=source || x.ProviderProfile!=profile || x.Status!=PluginConnectionStatus.Revoked ||
                x.ExternalAccountId is not null || x.BoundResourceId is not null || x.ActiveCredentialAttemptId is not null || x.RefreshLeaseOwner is not null || x.RefreshLeaseUntil is not null) ||
            attempts.Any(x=>x.PluginInstallationId!=source || x.IsMaintenance || x.ExpiresAt>now ||
                x.Status is not ("Expired" or "Cancelled" or "ImportFailed") || x.VerifiedSubject is not null || x.IdentityAuthority is not null ||
                x.PagesImported!=0 || x.ObservedResourcesJson!="[]" || x.RetiredCredentialKey is not null || x.LeaseOwner is not null || x.LeaseUntil is not null ||
                !connections.Any(c=>c.Id==x.ConnectionId && c.AccountGroupId==x.AccountGroupId)) ||
            connections.Any(c=>!attempts.Any(a=>a.ConnectionId==c.Id)) ||
            await db.PluginAccountResourceAuthorizations.AnyAsync(x=>connectionIds.Contains(x.ConnectionId),ct) ||
            await db.PluginAccountResources.AnyAsync(x=>ids.Contains(x.AccountGroupId),ct) ||
            await db.PluginAccountBusinessGrants.AnyAsync(x=>ids.Contains(x.AccountGroupId),ct))
            throw Changed("Another account still has identity, access or unresolved setup; compatible adoption is unavailable.");
        // The existing exact secret-inventory check below still permits only the active
        // connection's protected reference. No historical credential can follow this path.
        return Hash(new{Groups=groups.OrderBy(x=>x.Id).ToArray(),Connections=connections,
            Attempts=attempts.Select(x=>new{x.Id,x.PluginInstallationId,x.AccountGroupId,x.ConnectionId,x.Revision,x.Status,x.IsMaintenance,
                x.ExpiresAt,x.VerifiedSubject,x.IdentityAuthority,x.PagesImported,x.ObservedResourcesJson,x.RetiredCredentialKey,x.LeaseOwner,x.LeaseUntil}).ToArray()});
    }

    private async Task<PluginAccountUpgrade> Intent(Guid user,Guid target,Guid id,CancellationToken ct)
    {await Access.RequireAdministratorAsync(user,ct);return await db.PluginAccountUpgrades.SingleOrDefaultAsync(x=>x.Id==id && x.TargetInstallationId==target && x.ApplicationUserId==user,ct)
        ??throw new UnauthorizedAccessException("This upgrade intent belongs to another administrator.");}
    private async Task LockSource(Guid id,CancellationToken ct)
    {if(db.Database.IsNpgsql())_ = await db.AgentInstallations.FromSqlInterpolated($"SELECT * FROM \"AgentInstallations\" WHERE \"Id\" = {id} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);}
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> Transaction(CancellationToken ct)
        =>db.Database.IsRelational()?await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct):null;
    private static void RequireReceipt(PluginAccountUpgrade row,ConnectionAdoptionRequest request,ConnectionAdoptionReceipt receipt)
    {if(receipt.AdoptionId!=row.Id || receipt.ConnectionId!=row.RemoteConnectionId || receipt.RequestHash!=Hash(request) || receipt.ReviewHash!=row.ServiceReviewHash ||
        receipt.CompatibilityHash!=row.CompatibilityHash || receipt.Outcome!="Adopted" || receipt.AdoptedRevision!=request.ExpectedConnectionRevision+1 ||
        receipt.CurrentStatus!="Ready" || receipt.CurrentRevision<receipt.AdoptedRevision || receipt.CurrentInstallationId!=row.TargetInstallationId ||
        receipt.CurrentPackageDigest!=request.TargetPackageDigest)throw Changed("The current service receipt cannot authorize this adoption.");}
    private static string Hash<T>(T value)=>CompatibleConnectionContract.Hash(JsonSerializer.SerializeToElement(value,Json));
    private static PluginAccountUpgradeProgress Progress(PluginAccountUpgrade row)=>new(row.Id,row.SourceInstallationId,row.TargetInstallationId,row.Status);
    private static InvalidOperationException Changed(string message="Review the current compatible package and account state again.")=>new(message);
}
