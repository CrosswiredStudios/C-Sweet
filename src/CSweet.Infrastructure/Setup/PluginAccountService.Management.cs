using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    public async Task<PluginInstallationImpact> GetInstallationImpactAsync(Guid actor,Guid installationId,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(actor,ct);
        var (installation,_,_)=await Context(installationId,ct,true);
        var accounts=await db.PluginAccountGroups.AsNoTracking().Where(x=>x.PluginInstallationId==installationId && x.State!=PluginAccountState.Disconnected).OrderBy(x=>x.Id).ToArrayAsync(ct);
        var impacts=new List<PluginAccountImpactView>();
        foreach(var account in accounts) impacts.Add(await GetDisconnectImpactAsync(actor,installationId,account.Id,ct));
        var administration=await Administration(installation,ct);
        var hash=ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(new { installation.Id,installation.IsEnabled,installation.RevisionStatus,
            installation.PackageVersion!.PackageDigest,administration.Revision,accounts=impacts.Select(x=>x.ReviewHash) }));
        return new(hash,impacts.SelectMany(x=>x.Businesses).Distinct().ToArray(),impacts.SelectMany(x=>x.Agents).Distinct().ToArray(),accounts.Length);
    }

    public async Task ControlInstallationAsync(Guid actor,Guid installationId,PluginInstallationControlRequest request,CancellationToken ct)
    {
        await Access.RequireAdministratorAsync(actor,ct);
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
        if(db.Database.IsNpgsql()) _=await db.AgentInstallations.FromSqlInterpolated($"SELECT * FROM \"AgentInstallations\" WHERE \"Id\" = {installationId} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        var impact=await GetInstallationImpactAsync(actor,installationId,ct);
        if(request.ReviewHash!=impact.ReviewHash || request.Action is not ("Pause" or "Resume" or "Remove"))
            throw new InvalidOperationException("Plugin access changed. Review the current impact again.");
        using var controlScope=db.BeginProviderDataErasure();
        var state=await db.PluginAccountAdministrations.SingleOrDefaultAsync(x=>x.PluginInstallationId==installationId,ct);
        if(request.ExpectedRevision!=(state?.Revision ?? 1)) throw new InvalidOperationException("The plugin controls changed. Refresh and review again.");
        if(state==null) { state=new(){PluginInstallationId=installationId}; db.PluginAccountAdministrations.Add(state); }
        if(state.RemovalRequestedAt!=null || (await db.AgentInstallations.SingleAsync(x=>x.Id==installationId,ct)).RevisionStatus!=PluginRevisionStatus.Active)
            throw new InvalidOperationException("This plugin is already being removed or replaced.");
        var installation=await db.AgentInstallations.Include(x=>x.Schedule).SingleAsync(x=>x.Id==installationId,ct);
        if(request.Action=="Resume")
        {
            if((await Context(installationId,ct,true)).Installation.PackageVersion!.Status!=AgentPackageVersionStatus.Built)
                throw new InvalidOperationException("The verified build must be ready before resuming this plugin.");
            if(!installation.IsEnabled) installation.IsEnabled=true;
            if(installation.Schedule!=null) installation.Schedule.IsEnabled=true;
        }
        else
        {
            if(request.Action=="Remove")
            {
                foreach(var account in await db.PluginAccountGroups.Where(x=>x.PluginInstallationId==installationId && x.State!=PluginAccountState.Disconnected && x.State!=PluginAccountState.Disconnecting).ToArrayAsync(ct))
                { var current=await GetDisconnectImpactAsync(actor,installationId,account.Id,ct); await DisconnectAsync(actor,installationId,account.Id,new(current.AccountRevision,current.ReviewHash),ct); }
                state.RemovalRequestedAt=DateTimeOffset.UtcNow;
                foreach(var op in await db.PluginAccountOperators.Where(x=>x.PluginInstallationId==installationId && x.RevokedAt==null).ToArrayAsync(ct)) { op.RevokedAt=DateTimeOffset.UtcNow; op.Revision++; }
            }
            installation.IsEnabled=false;
            if(installation.Schedule!=null) { installation.Schedule.IsEnabled=false; installation.Schedule.NextTickAt=null; }
            foreach(var execution in await db.ConnectorExecutions.Where(x=>x.ConnectorInstallationId==installationId && (x.Status=="Prepared" || x.Status=="Approved")).ToArrayAsync(ct))
            { execution.Status="Cancelled"; execution.Revision++; }
            var bindings=await db.AgentCapabilityBindings.AsNoTracking().Where(x=>x.ProviderInstallationId==installationId && x.RevokedAt==null).ToArrayAsync(ct);
            foreach(var business in bindings.GroupBy(x=>x.OrganizationId))
                if(Guid.TryParse(business.Key,out var org))
                {
                    var consumers=business.Select(x=>x.RequesterInstallationId).Distinct().ToArray();
                    await ConnectorStandingPolicyService.RevokeForConsumersAsync(db,org,consumers,ct);
                    foreach(var policy in await db.PluginStandingPolicies.Where(x=>x.OrganizationId==org && consumers.Contains(x.AgentInstallationId) && x.Status==PluginStandingPolicyStatus.Approved).ToArrayAsync(ct))
                    { policy.Status=PluginStandingPolicyStatus.Revoked; policy.RevokedAt=DateTimeOffset.UtcNow; }
                }
        }
        state.Revision++; installation.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin-account.installation.controlled",nameof(AgentInstallation),installationId,
            $"Administrator requested {request.Action} after reviewing the current impact. External effects already sent are not undone; autonomous policies are not restored.",cancellationToken:ct);
        if(tx!=null) await tx.CommitAsync(ct);
        await FinalizeRemovals(ct);
    }

    private async Task FinalizeRemovals(CancellationToken ct)
    {
        foreach(var state in await db.PluginAccountAdministrations.Where(x=>x.RemovalRequestedAt!=null).ToArrayAsync(ct))
        {
            if(await db.PluginAccountGroups.AnyAsync(x=>x.PluginInstallationId==state.PluginInstallationId && x.State!=PluginAccountState.Disconnected,ct)) continue;
            var installation=await db.AgentInstallations.SingleAsync(x=>x.Id==state.PluginInstallationId,ct);
            installation.IsEnabled=false; installation.RevisionStatus=PluginRevisionStatus.Retired;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RequireAdministrationAsync(Guid actor,Guid installationId,CancellationToken ct)
    { await Access.RequireAdministratorAsync(actor,ct); await Context(installationId,ct,true); }
}
