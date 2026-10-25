using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    private async Task<PluginAdministrationView> Administration(AgentInstallation installation,CancellationToken ct)
    {
        var state=await db.PluginAccountAdministrations.AsNoTracking().SingleOrDefaultAsync(x=>x.PluginInstallationId==installation.Id,ct);
        var operators=await (from o in db.PluginAccountOperators.AsNoTracking() join u in db.Users.AsNoTracking() on o.ApplicationUserId equals u.Id
            where o.PluginInstallationId==installation.Id
            select new PluginAccountOperatorView(o.Id,u.Id,u.UserName ?? "User",o.AccountGroupId,o.Revision,o.RevokedAt!=null)).ToArrayAsync(ct);
        var candidates=await db.Users.AsNoTracking().Where(u=>!u.LockoutEnabled || u.LockoutEnd==null || u.LockoutEnd<=DateTimeOffset.UtcNow)
            .OrderBy(u=>u.UserName).Take(200).Select(u=>new PluginOperatorCandidate(u.Id,u.UserName ?? "User")).ToArrayAsync(ct);
        return new(installation.IsEnabled,installation.RevisionStatus==PluginRevisionStatus.Retired,state?.RemovalRequestedAt!=null,state?.Revision ?? 1,operators,candidates)
        { RepositoryUrl=await db.AgentPackageSources.Where(x=>x.Id==installation.PackageVersion!.PackageSourceId).Select(x=>x.RepositoryUrl).SingleOrDefaultAsync(ct),
          PreviousInstallationId=installation.SupersedesInstallationId,Staged=installation.RevisionStatus==PluginRevisionStatus.Staged,
          DevelopmentPreview=DevelopmentPreview,MissingDeletionStores=MissingDeletionStores };
    }

    public async Task SaveOperatorAsync(Guid actor,Guid installationId,SavePluginOperatorRequest request,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(actor,ct);
        var (installation,_,declaration)=await Context(installationId,ct,true);
        if(installation.RevisionStatus!=PluginRevisionStatus.Active || request.PackageDigest!=installation.PackageVersion!.PackageDigest ||
            await db.PluginAccountAdministrations.AnyAsync(x=>x.PluginInstallationId==installationId && x.RemovalRequestedAt!=null,ct))
            throw new InvalidOperationException("Refresh and review the active plugin before changing operators.");
        if(!await Access.IsActiveHumanAsync(request.UserId,ct) || request.AccountId is { } account &&
            !await db.PluginAccountGroups.AnyAsync(x=>x.Id==account && x.PluginInstallationId==installationId && x.State==PluginAccountState.Connected,ct))
            throw new UnauthorizedAccessException("Choose an active user and an available account.");
        var profile=await profiles.ResolveAsync(declaration.ProviderProfile,ct) ?? throw new InvalidOperationException("An administrator must configure the deployment connection profile first.");
        if(installation.PackageVersion!.Status!=AgentPackageVersionStatus.Built || profile.AuthorizationEndpoint!=declaration.Provider!.AuthorizationEndpoint ||
            profile.TokenEndpoint!=declaration.Provider.TokenEndpoint || profile.RevocationEndpoint!=declaration.Provider.RevocationEndpoint)
            throw new InvalidOperationException("The deployment profile must match the reviewed, completed plugin build.");
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
        var row=await db.PluginAccountOperators.SingleOrDefaultAsync(x=>x.PluginInstallationId==installationId && x.ApplicationUserId==request.UserId && x.AccountGroupId==request.AccountId,ct);
        if(request.ExpectedRevision!=row?.Revision) throw new InvalidOperationException("Operator access changed. Refresh and review again.");
        if(row==null) { row=new(){Id=Guid.NewGuid(),PluginInstallationId=installationId,ApplicationUserId=request.UserId,AccountGroupId=request.AccountId}; db.PluginAccountOperators.Add(row); }
        row.GrantedByApplicationUserId=actor; row.RevokedAt=null; row.Revision++;
        var approval=await db.ConnectorProfileApprovals.SingleOrDefaultAsync(x=>x.ConnectorInstallationId==installationId && x.PackageDigest==request.PackageDigest && x.ProfileId==profile.Id,ct);
        if(approval==null) { approval=new(){Id=Guid.NewGuid(),ConnectorInstallationId=installationId,PackageDigest=request.PackageDigest,ProfileId=profile.Id}; db.ConnectorProfileApprovals.Add(approval); }
        approval.ApprovedByApplicationUserId=actor; approval.ApprovedAt=DateTimeOffset.UtcNow; approval.RevokedAt=null; approval.ProviderProfileHash=PluginAccountProfileGuard.Fingerprint(profile);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin-account.operator.assigned",nameof(PluginAccountOperator),row.Id,
            "An administrator explicitly assigned connection-operator authority. Business grants and agent permissions are unchanged.",cancellationToken:ct);
        if(tx!=null) await tx.CommitAsync(ct);
    }

    public async Task RevokeOperatorAsync(Guid actor,Guid installationId,Guid operatorId,long revision,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(actor,ct);
        var row=await db.PluginAccountOperators.SingleOrDefaultAsync(x=>x.Id==operatorId && x.PluginInstallationId==installationId,ct) ?? throw new UnauthorizedAccessException();
        if(row.Revision!=revision) throw new InvalidOperationException("Operator access changed. Refresh and review again.");
        row.RevokedAt=DateTimeOffset.UtcNow; row.Revision++;
        await db.SaveChangesAsync(ct);
        var remaining=await db.PluginAccountOperators.AsNoTracking().Where(x=>x.PluginInstallationId==installationId && x.ApplicationUserId==row.ApplicationUserId && x.RevokedAt==null).ToArrayAsync(ct);
        var administrator=await Access.IsAdministratorAsync(row.ApplicationUserId,ct);
        foreach(var attempt in await db.PluginAccountSetupSessions.Where(x=>x.PluginInstallationId==installationId && x.ApplicationUserId==row.ApplicationUserId && !x.IsMaintenance &&
            (x.Status=="AwaitingConsent" || x.Status=="Importing" || x.Status=="ImportFailed")).ToArrayAsync(ct))
        {
            if(administrator || remaining.Any(x=>x.AccountGroupId==null || attempt.ReplacesConnectionId!=null && x.AccountGroupId==attempt.AccountGroupId)) continue;
            attempt.Status="Cancelled";attempt.Revision++;attempt.ObservedResourcesJson="[]";attempt.VerifiedSubject=null;attempt.VerifiedName=null;
            var connection=await db.PluginConnections.SingleAsync(x=>x.Id==attempt.ConnectionId,ct);
            connection.Status=PluginConnectionStatus.Revoked;connection.AuthorizationRevision++;
            await db.SaveChangesAsync(ct);
            await secrets.RemoveAsync(installationId,VerifierKey(attempt.Id),ct);
            var key=await PluginOAuthCredentialKeys.ResolveAsync(db,installationId,connection.Id,ct);
            await secrets.RemoveAsync(installationId,key,ct);
            // Do not revoke a shared provider grant merely because this operator lost local setup authority.
        }
        // Current() rechecks persisted authority at callback and every import request. Do not revoke shared account tokens.
        await audit.WriteAsync("plugin-account.operator.revoked",nameof(PluginAccountOperator),row.Id,
            "Connection-operator authority removed. Shared completed connections remain intact.",cancellationToken:ct);
    }
}
