using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Authorization is resolved from current server-owned organization and workstream records on every call.</summary>
public sealed class WebPreviewGrantService(CSweetDbContext db, TimeProvider clock, WebHostReleaseCatalog? releases = null) : IManagedActionExecutor
{
    public const string ActionType = "web-preview.grant";
    public const string PluginId = "com.csweet.web-previews";
    public bool CanExecute(string actionType) => actionType == ActionType;

    public async Task<PreviewGrantProposal> RequestAsync(Guid organizationId, Guid installationId,
        RequestPreviewGrant request, CancellationToken token)
    {
        var actor = await RequireActorAsync(organizationId,installationId,WebPreviewCapabilities.RequestGrant,token);
        await RequireWorkstreamAsync(organizationId,actor.Id,request.ProjectId,token);
        await RequireProviderAsync(organizationId,request.ProviderInstallationId,token);
        Validate(request,clock.GetUtcNow());
        if (await db.SourceControlRepositories.AsNoTracking().CountAsync(x => x.OrganizationId==organizationId &&
            request.RepositoryIds.Contains(x.Id) && x.ArchivedAt==null,token) != request.RepositoryIds.Count)
            throw new UnauthorizedAccessException("Every requested repository must belong to this organization and be active.");
        var digest=WorkloadAuthorizationEnvelope.Digest(JsonSerializer.Serialize(request,PreviewJson.Options));
        var existing=await db.WebPreviewGrants.AsNoTracking().SingleOrDefaultAsync(x=>x.OrganizationId==organizationId &&
            x.InstallationId==installationId && x.IdempotencyKey==request.IdempotencyKey,token);
        if(existing is not null)
        {
            if(existing.RequestDigest!=digest) throw new InvalidOperationException("The grant request key was reused with different terms.");
            return Map(existing);
        }
        if(await db.WebPreviewGrants.CountAsync(x=>x.OrganizationId==organizationId && x.InstallationId==installationId && x.Status=="Pending" && x.ExpiresAt>clock.GetUtcNow(),token)>=10)
            throw new InvalidOperationException("Resolve an existing preview grant request before creating another.");
        var workstreamName=await db.Workstreams.Where(x=>x.Id==request.ProjectId).Select(x=>x.Name).SingleAsync(token);
        var repositories=await db.SourceControlRepositories.Where(x=>request.RepositoryIds.Contains(x.Id) && x.OrganizationId==organizationId).Select(x=>x.Owner+"/"+x.Name).ToArrayAsync(token);
        var now=clock.GetUtcNow(); var id=Guid.NewGuid(); var artifactId=Guid.NewGuid(); var revisionId=Guid.NewGuid();
        var proposalId=Guid.NewGuid();
        var policy=new PreviewGrant(id,1,organizationId,request.ProjectId,installationId,request.ProviderInstallationId,
            [WebPreviewCapabilities.Preflight,WebPreviewCapabilities.Start,WebPreviewCapabilities.Read,
             WebPreviewCapabilities.Stop,WebPreviewCapabilities.Diagnostics,WebPreviewCapabilities.Renew,WebPreviewCapabilities.Test,WebPreviewCapabilities.Build],
            request.RepositoryIds,request.MaximumResources,request.MaximumConcurrentPreviews,request.MaximumCpuSeconds,
            request.MaximumLifetimeSeconds,[],request.ExpiresAt,false);
        var policyJson=JsonSerializer.Serialize(policy,PreviewJson.Options);
        var content=$"""
            # Private web preview grant

            Requested by: {actor.DisplayName}
            Purpose: {request.Reason}

            This grants this agent access to the listed repositories for team-only product previews in this Workstream.
            Each preview is limited to {request.MaximumResources.CpuCount} CPU cores, {request.MaximumResources.MemoryMb} MiB memory,
            {request.MaximumResources.DiskMb} MiB disposable disk and {request.MaximumLifetimeSeconds} seconds.
            Up to {request.MaximumConcurrentPreviews} previews may run at once. Total reserved CPU time may not exceed
            {request.MaximumCpuSeconds} CPU-seconds before the grant expires at {request.ExpiresAt:O}.
            External connections, public publishing and production hosting are excluded.

            Exact grant terms:
            {policyJson}
            """;
        var contentDigest=WorkloadAuthorizationEnvelope.Digest(content);
        var artifact=new Artifact { Id=artifactId,OrganizationId=organizationId,WorkstreamId=request.ProjectId,
            Type=ArtifactType.Decision,Title="Private web preview access",Content=content,Version=1,
            ApprovalStatus=ApprovalStatus.Pending,DocumentStatus=ArtifactDocumentStatus.InReview,DocumentType=ActionType,
            CreatedByOrganizationUserId=actor.Id,CreatorDisplayName=actor.DisplayName,CreatedAt=now,UpdatedAt=now,
            LatestRevisionId=revisionId,SubmittedRevisionId=revisionId };
        var revision=new ArtifactRevision { Id=revisionId,OrganizationId=organizationId,ArtifactId=artifactId,
            Number=1,Content=content,ContentSha256=contentDigest[7..],Status=ArtifactRevisionStatus.Submitted,
            CreatedByOrganizationUserId=actor.Id,CreatedByAgentInstallationId=installationId,
            CreatorDisplayName=actor.DisplayName,IdempotencyKey="web-preview:"+id.ToString("N"),CreatedAt=now,SubmittedAt=now };
        var record=new WebPreviewGrantRecord { Id=id,OrganizationId=organizationId,WorkstreamId=request.ProjectId,
            InstallationId=installationId,ProviderInstallationId=request.ProviderInstallationId,PolicyJson=policyJson,
            ApprovalArtifactId=artifactId,ApprovalRevisionId=revisionId,ApprovalProposalId=proposalId,
            ApprovalContentDigest=contentDigest,RequestedByOrganizationUserId=actor.Id,IdempotencyKey=request.IdempotencyKey,
            RequestDigest=digest,CreatedAt=now,ExpiresAt=request.ExpiresAt };
        db.CoreArtifacts.Add(artifact); db.ArtifactRevisions.Add(revision); db.WebPreviewGrants.Add(record);
        db.ActionProposals.Add(new ActionProposal { Id=proposalId,OrganizationId=organizationId,AgentInstallationId=installationId,
            ActionType=ActionType,Summary=$"Allow private web previews: {request.MaximumConcurrentPreviews} simultaneous previews; "+
                $"{request.MaximumResources.CpuCount} CPU, {request.MaximumResources.MemoryMb} MiB memory each; expires {request.ExpiresAt:O}.",
            PayloadJson=JsonSerializer.Serialize(new { actionType=ActionType,channelId=request.ProviderInstallationId.ToString("D"),
                resourceId=id.ToString("D"),expectedRevision=1,payloadHash=WorkloadAuthorizationEnvelope.Digest(policyJson),
                idempotencyKey=request.IdempotencyKey,change=new { expiresAt=request.ExpiresAt, grant=policy },
                accountName="Web Previews",
                reviewPayload=new { request.Reason,workstreamName,repositories,resources=request.MaximumResources,
                    request.MaximumConcurrentPreviews,request.MaximumCpuSeconds,request.MaximumLifetimeSeconds,request.ExpiresAt },
                approvalArtifactId=artifactId,approvalRevisionId=revisionId },PreviewJson.Options),
            RiskClass="ScopedPermission",IdempotencyKey="web-preview-grant:"+id.ToString("N"),
            Status=ProposalStatus.Pending,CreatedAt=now });
        await db.SaveChangesAsync(token);
        return Map(record);
    }
    public async Task<ManagedActionExecutionResult> ExecuteAsync(ActionProposal proposal, OrganizationUser approvingActor,
        CancellationToken cancellationToken=default)
    {
        var token=cancellationToken;
        if(!CanExecute(proposal.ActionType)) throw new InvalidOperationException("Unsupported managed action.");
        var owner=await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==approvingActor.Id &&
            x.OrganizationId==proposal.OrganizationId && x.IsActive && x.ArchivedAt==null &&
            x.PermissionLevel==OrganizationPermissionLevel.Owner && x.EmployeeType==EmployeeType.Human &&
            x.ApplicationUserId!=null,token) ?? throw new UnauthorizedAccessException("A current human business owner must approve web hosting grants.");
        var record=await db.WebPreviewGrants.SingleOrDefaultAsync(x=>x.ApprovalProposalId==proposal.Id &&
            x.OrganizationId==proposal.OrganizationId && x.InstallationId==proposal.AgentInstallationId,token)
            ?? throw new InvalidOperationException("The exact grant proposal is unavailable.");
        if(record.Status=="Active") return new(record.Id,record.Revision,"This exact web preview grant is already active.");
        if(proposal.Status!=ProposalStatus.Pending || record.Status!="Pending" || record.RevokedAt is not null ||
            record.ExpiresAt<=clock.GetUtcNow()) throw new InvalidOperationException("The grant request is no longer pending.");
        var actor=await RequireActorAsync(record.OrganizationId,record.InstallationId,WebPreviewCapabilities.RequestGrant,token);
        await RequireWorkstreamAsync(record.OrganizationId,actor.Id,record.WorkstreamId,token);
        await RequireProviderAsync(record.OrganizationId,record.ProviderInstallationId,token);
        using var binding=JsonDocument.Parse(proposal.PayloadJson);
        if(binding.RootElement.GetProperty("payloadHash").GetString()!=WorkloadAuthorizationEnvelope.Digest(record.PolicyJson) ||
            binding.RootElement.GetProperty("resourceId").GetString()!=record.Id.ToString("D") ||
            binding.RootElement.GetProperty("expectedRevision").GetInt64()!=record.Revision)
            throw new InvalidOperationException("The preview grant changed after review.");
        var artifact=await db.CoreArtifacts.SingleAsync(x=>x.Id==record.ApprovalArtifactId && x.OrganizationId==record.OrganizationId,token);
        var revision=await db.ArtifactRevisions.SingleAsync(x=>x.Id==record.ApprovalRevisionId && x.ArtifactId==artifact.Id,token);
        if(artifact.LatestRevisionId!=revision.Id || artifact.ArchivedAt is not null ||
            WorkloadAuthorizationEnvelope.Digest(revision.Content)!=record.ApprovalContentDigest ||
            WorkloadAuthorizationEnvelope.Digest(artifact.Content)!=record.ApprovalContentDigest)
            throw new InvalidOperationException("The reviewed grant document changed; create a new proposal.");
        var policy=JsonSerializer.Deserialize<PreviewGrant>(record.PolicyJson,PreviewJson.Options)!;
        if(await db.SourceControlRepositories.CountAsync(x=>x.OrganizationId==record.OrganizationId &&
            policy.RepositoryIds.Contains(x.Id) && x.ArchivedAt==null,token)!=policy.RepositoryIds.Count)
            throw new InvalidOperationException("A requested source repository is no longer available.");
        var now=clock.GetUtcNow();
        record.Status="Active"; record.ApprovedByOrganizationUserId=owner.Id; record.Revision++;
        record.PolicyJson=JsonSerializer.Serialize(policy with { Revision=record.Revision },PreviewJson.Options);
        artifact.ApprovalStatus=ApprovalStatus.Approved; artifact.DocumentStatus=ArtifactDocumentStatus.Approved;
        artifact.AcceptedRevisionId=revision.Id; artifact.SubmittedRevisionId=null; artifact.UpdatedAt=now;
        revision.Status=ArtifactRevisionStatus.Accepted; revision.DecidedAt=now;
        db.CoreApprovals.Add(new Approval { Id=Guid.NewGuid(),ArtifactId=artifact.Id,ArtifactRevisionId=revision.Id,
            Status=ApprovalStatus.Approved,CreatedAt=now,DecidedAt=now,DecidedByOrganizationUserId=owner.Id,
            Comment="Approved the exact private web preview grant through the business approvals dashboard." });
        // The standard approval endpoint commits grant activation and the proposal decision together.
        return new(record.Id,record.Revision,"Private web preview access was approved within the reviewed limits.");
    }
    public async Task<PreviewPreflight> PreflightAsync(Guid organizationId,Guid installationId,PreviewRequest request,CancellationToken token)
    {
        var actor=await RequireActorAsync(organizationId,installationId,WebPreviewCapabilities.Preflight,token);
        await RequireWorkstreamAsync(organizationId,actor.Id,request.ProjectId,token);
        await RequireProviderAsync(organizationId,request.ProviderInstallationId,token);
        var problems=ManifestValidator.Validate(request.Manifest).Where(x => x.Field != "artifactDigest" || request.BuildId is null || request.Manifest.ArtifactDigest is not null).ToList();
        var digest=WorkloadAuthorizationEnvelope.Digest(JsonSerializer.Serialize(request.Manifest,PreviewJson.Options));
        if(problems.Count>0) return new(false,problems,digest);
        var now=clock.GetUtcNow();
        var grants=await db.WebPreviewGrants.AsNoTracking().Where(x=>x.OrganizationId==organizationId &&
            x.InstallationId==installationId && x.WorkstreamId==request.ProjectId &&
            x.ProviderInstallationId==request.ProviderInstallationId && x.Status=="Active" && x.RevokedAt==null && x.ExpiresAt>now)
            .OrderBy(x=>x.CreatedAt).ToListAsync(token);
        if(grants.Count==0) return new(false,[new("GrantRequired","grant",
            "Request a bounded hosting grant with web-preview.grant.request.v1. The business owner can review it in Approvals.")],digest);
        var active=await db.WebPreviewJobs.AsNoTracking().CountAsync(x=>x.OrganizationId==organizationId &&
            x.WorkstreamId==request.ProjectId && x.TeardownConfirmedAt==null,token);
        PreviewPreflight? result=null;
        foreach(var grant in grants)
        {
            var policy=JsonSerializer.Deserialize<PreviewGrant>(grant.PolicyJson,PreviewJson.Options)
                ?? throw new InvalidDataException("The stored preview grant is invalid.");
            result=PreviewPolicy.Evaluate(organizationId,installationId,request,policy,WebPreviewCapabilities.Start,
                active,grant.ReservedCpuSeconds,now);
                        // A successful immutable build supplies the final digest during admission; preflight
            // may inspect its policy without pretending an unverified digest authorizes execution.
            if (request.BuildId is { } buildId && buildId != Guid.Empty && request.Manifest.ArtifactDigest is null &&
                await db.DeliveryBuilds.AsNoTracking().AnyAsync(x => x.Id == buildId && x.OrganizationId == organizationId &&
                    x.WorkstreamId == request.ProjectId && x.RepositoryId == request.RepositoryId &&
                    x.SourceRevision == request.Manifest.SourceRevision && x.Status == "Succeeded", token))
            {
                var remaining = result.Problems.Where(x => x.Field != "artifactDigest").ToArray();
                result = result with { Problems = remaining, Allowed = remaining.Length == 0 };
            }
            if(result.Allowed) break;
        }
        if (result!.Allowed && releases is not null)
        {
            var hosts = await db.WebHostRegistrations.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.ProviderInstallationId == request.ProviderInstallationId && x.RevokedAt == null).ToListAsync(token);
            if (hosts.Any(x => releases.Select(x, now.AddSeconds(request.Manifest.LifetimeSeconds)) is not null &&
                request.Manifest.Resources.Fits(JsonSerializer.Deserialize<WebHostHeartbeat>(x.ReportedHeartbeatJson!, PreviewJson.Options)!.Available)))
                return result;
        }
        return result with { Allowed=false,Problems=[..result.Problems,
            new("RuntimeUnavailable","host","A configured certified WebHost with capacity and a supported build is required.")] };
    }    public async Task<PreviewGrantProposal> RevokeAsync(Guid organizationId,Guid grantId,Guid applicationUserId,CancellationToken token)
    {
        if(!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x=>x.OrganizationId==organizationId &&
            x.ApplicationUserId==applicationUserId && x.IsActive && x.ArchivedAt==null &&
            x.EmployeeType==EmployeeType.Human && x.PermissionLevel==OrganizationPermissionLevel.Owner,token))
            throw new UnauthorizedAccessException("Only the current human business owner can revoke hosting grants.");
        var record=await db.WebPreviewGrants.SingleOrDefaultAsync(x=>x.Id==grantId && x.OrganizationId==organizationId,token)
            ?? throw new UnauthorizedAccessException("The preview grant is not available.");
        if(record.RevokedAt is not null) return Map(record);
        record.RevokedAt=clock.GetUtcNow(); record.Status="Revoked"; record.Revision++;
        var policy=JsonSerializer.Deserialize<PreviewGrant>(record.PolicyJson,PreviewJson.Options)!;
        record.PolicyJson=JsonSerializer.Serialize(policy with { Revoked=true,Revision=record.Revision },PreviewJson.Options);
        foreach(var job in await db.WebPreviewJobs.Where(x=>x.GrantId==grantId && x.OrganizationId==organizationId &&
            x.Phase!="Stopped" && x.Phase!="Expired" && x.Phase!="Failed" && x.Phase!="Revoked").ToListAsync(token))
        {
            job.Phase="Stopping"; job.FailureCode="GrantRevoked"; job.AccessReference=null;
            job.UpdatedAt=clock.GetUtcNow(); job.Revision++;
        }
        await db.SaveChangesAsync(token);
        return Map(record);
    }
    internal async Task<OrganizationUser> RequireActorAsync(Guid organizationId,Guid installationId,string capability,CancellationToken token)
    {
        var installation=await db.AgentInstallations.AsNoTracking().Include(x=>x.Grant).SingleOrDefaultAsync(x=>x.Id==installationId &&
            x.IsEnabled && x.RevisionStatus==PluginRevisionStatus.Active && x.SetupState==PluginSetupState.Ready,token);
        if(installation is null || (!Guid.TryParse(installation.BusinessId,out var businessId) || businessId!=organizationId) ||
            JsonSerializer.Deserialize<string[]>(installation.Grant?.RequiredCapabilitiesJson??"[]")?.Contains(capability,StringComparer.Ordinal)!=true)
            throw new UnauthorizedAccessException("The current installation does not have this preview capability.");
        return await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.OrganizationId==organizationId &&
            x.AgentInstallationId==installationId && x.IsActive && x.ArchivedAt==null && x.EmployeeType==EmployeeType.Agent,token)
            ?? throw new UnauthorizedAccessException("An active agent employee is required.");
    }
    internal async Task RequireProviderAsync(Guid organizationId,Guid providerId,CancellationToken token)
    {
        var provider=await db.AgentInstallations.AsNoTracking().Include(x=>x.PackageVersion).SingleOrDefaultAsync(x=>x.Id==providerId &&
            x.IsEnabled && x.RevisionStatus==PluginRevisionStatus.Active && x.SetupState==PluginSetupState.Ready,token);
        if(provider?.PackageVersion?.AgentId!=PluginId ||
            (provider.Scope==PluginInstallationScope.Organization && provider.BusinessId!=organizationId.ToString("D")) ||
            (provider.Scope==PluginInstallationScope.System && !await db.PluginOrganizationGrants.AsNoTracking()
                .AnyAsync(x=>x.PluginInstallationId==providerId && x.OrganizationId==organizationId,token)))
            throw new UnauthorizedAccessException("Install and enable the optional Web Previews plugin for this organization.");
    }
    internal async Task RequireWorkstreamAsync(Guid organizationId,Guid actorId,Guid workstreamId,CancellationToken token)
    {
        var workstream=await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==workstreamId && x.OrganizationId==organizationId,token)
            ?? throw new UnauthorizedAccessException("The Workstream is not available.");
        if(workstream.AccountableManagerOrganizationUserId==actorId || await db.WorkstreamSupervisionAssignments.AsNoTracking()
            .AnyAsync(x=>x.WorkstreamId==workstreamId && x.SupervisorOrganizationUserId==actorId && x.EndsAt==null,token)) return;
        var teams=await db.WorkstreamTeamAssignments.AsNoTracking().Where(x=>x.WorkstreamId==workstreamId && x.EndsAt==null).Select(x=>x.TeamId).ToListAsync(token);
        if(!await db.TeamMemberships.AsNoTracking().AnyAsync(x=>teams.Contains(x.TeamId) && x.OrganizationUserId==actorId && x.EndedAt==null,token))
            throw new UnauthorizedAccessException("The Workstream is outside this employee's scope.");
    }
    private static void Validate(RequestPreviewGrant request,DateTimeOffset now)
    {
        if(request.ProjectId==Guid.Empty || request.ProviderInstallationId==Guid.Empty ||
            request.RepositoryIds is not { Count: >0 and <=100 } || request.RepositoryIds.Any(x=>x==Guid.Empty) ||
            request.RepositoryIds.Distinct().Count()!=request.RepositoryIds.Count || request.MaximumResources is null ||
            request.MaximumConcurrentPreviews<1 || request.MaximumCpuSeconds<1 || request.MaximumLifetimeSeconds<300 ||
            request.ExpiresAt<=now || request.ExpiresAt>now.AddDays(90) ||
            request.Reason is not { Length: >0 and <=2000 } || request.IdempotencyKey is not { Length: >0 and <=200 })
            throw new ArgumentException("Supply bounded resources, active repositories, a reason, a stable request key, and an expiry within 90 days.");
        request.MaximumResources.Validate();
    }
    private static PreviewGrantProposal Map(WebPreviewGrantRecord x)=>new(x.Id,x.ApprovalArtifactId,x.ApprovalRevisionId,x.Status,x.ExpiresAt,x.ApprovalProposalId);
}
