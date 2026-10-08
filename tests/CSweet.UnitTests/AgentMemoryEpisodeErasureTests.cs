using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task EpisodeErasureChecksHeldContributorOutsideTheErasedEpisodeTargets()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode,_)=await SeedPartialLegacyAsync(fixture); await using var db=fixture.Context();
        await SeedRecallTurnAsync(fixture);
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(),episode.Partition,fixture.MessageId,"Held contributor","Retained context",null,
            1,MemoryTrustTier.ConfirmedUser,MemoryConfirmationState.Confirmed,episode.OccurredAt,null,DateTimeOffset.UtcNow)
            { SourceEpisodeIds=[episode.Id,fixture.MessageId] });
        var recovery=IngestionRecovery(fixture,db); var review=await recovery.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await recovery.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),review.Revision,review.EvidenceToken,review.RequiredReconciliationPolicy));
        db.ChangeTracker.Clear();
        var inventory=await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(episode.Partition,episode.Id);
        Assert.DoesNotContain(inventory.Targets,x=>x.Kind==MemoryErasureKind.Episode && x.Id==fixture.MessageId);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id={fixture.MessageId}");
        var preview=await ErasureService(fixture,db).GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        Assert.Equal("memory_legal_hold_prevents_deletion",preview.ApplyBlockedReason); Assert.Null(preview.EvidenceToken);
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    private static async Task<(Guid User,Guid Target,Guid Copy)> SeedVerifiedErasureTransferAsync(DurabilityFixture fixture)
    {
        var(user,target,_)=await SeedTransferAsync(fixture);
        await using var db=fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x=>x.SetProperty(j=>j.AcceptedExtractionJson,(string?)null)
            .SetProperty(j=>j.ExtractionAcceptedAt,(DateTimeOffset?)null).SetProperty(j=>j.LastError,(string?)null)
            .SetProperty(j=>j.Status,MemoryCaptureStatus.Completed));
        (await db.AgentInstallations.SingleAsync(x=>x.Id==fixture.InstallationId)).SetupState=CSweet.Domain.Setup.PluginSetupState.Ready;
        await db.SaveChangesAsync();
        await fixture.Service(db,new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var now=DateTimeOffset.UtcNow; var entity=Guid.NewGuid();
        await fixture.Store.UpsertEntityAsync(new(entity,fixture.Partition,"person","Verified origin",[],null,false,now,now)
            { Sensitivity=MemorySensitivity.Personal,SourceEpisodeIds=[fixture.MessageId] });
        var claim=new MemoryClaim(Guid.NewGuid(),fixture.Partition,fixture.MessageId,entity,"prefers",null,"concise replies",
            MemoryTrustTier.ConfirmedUser,MemoryConfirmationState.Confirmed,MemorySensitivity.Personal,1,1,now,null,now)
            { SourceEpisodeIds=[fixture.MessageId] };
        await fixture.Store.WriteClaimAsync(claim);
        await db.Database.ExecuteSqlRawAsync(Infrastructure.Persistence.Migrations.DurableEpisodeMemoryEnrichment.InstallTriggers);
        await db.LlmProviderProfiles.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DefaultChatModel,"test-model"));
        var transfer=new AgentMemoryTransferService(db,fixture.Store,TimeProvider.System);
        var draft=await transfer.PrepareAsync(fixture.OrganizationId,fixture.EmployeeId,user,TransferRequest(target,claim));
        var review=await transfer.GetAsync(fixture.OrganizationId,fixture.EmployeeId,draft.PackageId,user);
        await transfer.TransitionAsync(fixture.OrganizationId,fixture.EmployeeId,draft.PackageId,user,new(Guid.NewGuid(),review.ReviewToken,"approve"));
        review=await transfer.GetAsync(fixture.OrganizationId,fixture.EmployeeId,draft.PackageId,user);
        var applied=await transfer.TransitionAsync(fixture.OrganizationId,fixture.EmployeeId,draft.PackageId,user,new(Guid.NewGuid(),review.ReviewToken,"apply"));
        return(user,target,applied.AppliedEpisodeId!.Value);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeErasureCleansAuthenticatedAppliedTransferWithItsVerifiedOrigin(bool completed)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,target,copy)=await SeedVerifiedErasureTransferAsync(fixture); await using var db=fixture.Context();
        if(completed) Assert.Equal(1,await fixture.Service(db,new ScriptedProviderFactory((_,_)=>Task.CompletedTask)).ProcessPendingAsync());
        db.ChangeTracker.Clear(); var service=ErasureService(fixture,db);
        var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,fixture.MessageId,user);
        Assert.Null(preview.ApplyBlockedReason); Assert.Equal(2,preview.Execution!.ExtractionJobs);
        var result=await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,fixture.MessageId,user,
            new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken)));
        Assert.Equal("completed",result.Status); Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(TransferTarget(fixture,target),copy));
        Assert.Single(await db.MemoryTransferReceipts.Where(x=>x.Action=="apply").ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("hold")]
    [InlineData("revoke")]
    [InlineData("replay")]
    public async Task EpisodeErasureReviewsTransferredContributorUpstreamRetentionAndReplayAudiences(string change)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,target,copy)=await SeedVerifiedErasureTransferAsync(fixture); await using var db=fixture.Context();
        var installation=(await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x=>x.Id==target)).AgentInstallationId!.Value;
        db.AgentInstallationGrants.Add(new() { Id=Guid.NewGuid(),AgentInstallationId=installation,RequiredCapabilitiesJson="[\"platform.memory.write.v1\"]" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(Infrastructure.Persistence.Migrations.ReviewedEpisodeReconciliation.InstallGuards);
        var now=DateTimeOffset.UtcNow; var id=Guid.NewGuid(); var partition=TransferTarget(fixture,target); const string content="My name is Alice.";
        var episode=new MemoryEpisode(id,partition,MemoryScope.User,content,"text/plain",new("agent-proposal",id.ToString("D"),target.ToString("D")),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),now,now,
            "agent-proposal:erasure:"+id.ToString("D"),Metadata:new Dictionary<string,string> { ["installationId"]=installation.ToString("D"),["employeeId"]=target.ToString("D") },
            Sensitivity:MemorySensitivity.Personal);
        await fixture.Store.AppendEpisodeAsync(episode);
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(),partition,copy,"Copied contributor","Retained procedure",null,
            1,MemoryTrustTier.ConfirmedUser,MemoryConfirmationState.Confirmed,now,null,now) { SourceEpisodeIds=[id,copy] });
        var recovery=IngestionRecovery(fixture,db); var review=await recovery.PreviewAsync(fixture.OrganizationId,target,id,user);
        Assert.True(review.CanQueue);
        await recovery.RecoverAsync(fixture.OrganizationId,target,id,user,new(Guid.NewGuid(),review.Revision,review.EvidenceToken,review.RequiredReconciliationPolicy));
        db.ChangeTracker.Clear();
        var inventory=await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(partition,id);
        Assert.DoesNotContain(inventory.Targets,x=>x.Kind==MemoryErasureKind.Episode && x.Id==fixture.MessageId);
        var service=ErasureService(fixture,db);
        if(change=="hold")
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id={fixture.MessageId}");
            Assert.False((await RetainedEpisodeAsync(fixture,copy)).LegalHold);
            var held=await service.GetErasureImpactAsync(fixture.OrganizationId,target,id,user);
            Assert.Equal("memory_legal_hold_prevents_deletion",held.ApplyBlockedReason); Assert.Null(held.EvidenceToken);
        }
        else
        {
            EraseMemorySourceRequest? request=null;
            if(change=="replay")
            {
                var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,target,id,user);
                request=new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken));
                await service.EraseSourceAsync(fixture.OrganizationId,target,id,user,request);
            }
            await db.CoreOrganizationUsers.Where(x=>x.Id==fixture.EmployeeId).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.ReportsToOrganizationUserId,(Guid?)null));
            if(request is null) await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetErasureImpactAsync(fixture.OrganizationId,target,id,user));
            else
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.EraseSourceAsync(fixture.OrganizationId,target,id,user,request));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetErasureStatusAsync(fixture.OrganizationId,target,request.OperationId,user));
            }
        }
        Assert.Equal(change=="replay" ? 1 : 2,await db.MemoryEpisodeEnrichmentJobs.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task EpisodeErasureSupportsVerifiedJoblessProposalWithoutCreatingAProcessingJob()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode)=await SeedJoblessProposalAsync(fixture); await using var db=fixture.Context();
        var service=ErasureService(fixture,db); var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        Assert.Null(preview.ApplyBlockedReason); Assert.Equal(0,preview.Execution!.ExtractionJobs);
        var result=await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken)));
        Assert.Equal("completed",result.Status); Assert.Equal(0,result.ClearedJobs);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition,episode.Id));
    }

    [MemoryPostgresFact]
    public async Task EpisodeErasurePreservesContentFreeRetryEvidenceAndDeniesRetryAfterCleanup()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,job)=await SeedEpisodeRecoveryAsync(fixture); await using var db=fixture.Context();
        var retry=EpisodeRecovery(fixture,db); var request=new RetryMemoryEnrichmentRequest(Guid.NewGuid(),0);
        await retry.RetryAsync(fixture.OrganizationId,fixture.EmployeeId,job,user,request);
        db.ChangeTracker.Clear(); var retained=JsonSerializer.Serialize(await db.MemoryEpisodeRetryReceipts.AsNoTracking().SingleAsync());
        var episode=await db.MemoryEpisodeEnrichmentJobs.Select(x=>x.EpisodeId).SingleAsync();
        var service=ErasureService(fixture,db); var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode,user);
        await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode,user,new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken)));
        Assert.Equal(retained,JsonSerializer.Serialize(await db.MemoryEpisodeRetryReceipts.AsNoTracking().SingleAsync()));
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>retry.RetryAsync(fixture.OrganizationId,fixture.EmployeeId,job,user,request));
    }
    private static async Task<(Guid User,MemoryEpisode Episode)> QueueEpisodeErasureAsync(DurabilityFixture fixture)
    {
        var (user,episode)=await SeedJoblessProposalAsync(fixture);
        await using var db=fixture.Context(); var recovery=IngestionRecovery(fixture,db);
        var preview=await recovery.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await recovery.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken));
        return(user,episode);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeErasureCleansReviewedSnapshotAndDerivativesAndReplaysWithoutResurrection(bool completed)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode)=await QueueEpisodeErasureAsync(fixture); await using var db=fixture.Context();
        if(completed) Assert.Equal(1,await fixture.Service(db,new ScriptedProviderFactory((_,_)=>Task.CompletedTask)).ProcessPendingAsync());
        db.ChangeTracker.Clear(); var service=ErasureService(fixture,db);
        var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        Assert.Null(preview.ApplyBlockedReason); Assert.Equal(1,preview.Execution!.ExtractionJobs);
        var request=new EraseMemorySourceRequest(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken));
        var result=await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,request);
        Assert.Equal("completed",result.Status); Assert.Equal(1,result.ClearedJobs);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.Empty(await db.MemoryEnrichmentProviderLeases.ToListAsync());
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition,episode.Id));
        Assert.Empty((await fixture.Store.ExportAsync(episode.Partition)).Claims);
        Assert.Single(await db.MemoryReviewReceipts.Where(x=>x.RecordKind=="EpisodeIngestion").ToListAsync());
        Assert.True((await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,request)).WasReplay);
        Assert.DoesNotContain("Alice",JsonSerializer.Serialize(await db.MemoryErasureReceipts.SingleAsync()));
        var error=await Assert.ThrowsAsync<PostgresException>(()=>fixture.Store.AppendEpisodeAsync(episode));
        Assert.Contains("memory_source_erased",error.MessageText);
    }

    [MemoryPostgresFact]
    public async Task EpisodeErasureRollsBackGenericCleanupWithStoreAndAudit()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode)=await QueueEpisodeErasureAsync(fixture); await using var db=fixture.Context();
        Assert.Equal(1,await fixture.Service(db,new ScriptedProviderFactory((_,_)=>Task.CompletedTask)).ProcessPendingAsync());
        db.ChangeTracker.Clear(); var original=JsonSerializer.Serialize(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync());
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_generic_erasure_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_generic_erasure_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW
              WHEN (NEW."SourceEntityType"='MemoryErasure') EXECUTE FUNCTION fail_generic_erasure_audit();
            """);
        var service=ErasureService(fixture,db); var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await Assert.ThrowsAsync<DbUpdateException>(()=>service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken))));
        Assert.Equal(original,JsonSerializer.Serialize(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()));
        Assert.Single(await db.MemoryEpisodeExtractionReceipts.ToListAsync()); Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition,episode.Id));
        Assert.Single((await fixture.Store.ExportAsync(episode.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task EpisodeErasureBindsGenericSnapshotValuesBeyondMatchingCounts()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode)=await QueueEpisodeErasureAsync(fixture); await using var db=fixture.Context();
        var service=ErasureService(fixture,db); var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x=>x.SetProperty(j=>j.LastError,"changed"));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
            new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken))));
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeErasureRejectsUnverifiedOrHeldRetainedInput(bool held)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode)=await QueueEpisodeErasureAsync(fixture); await using var db=fixture.Context();
        if(held) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id={episode.Id}");
        else
        {
            await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x=>x.SetProperty(j=>j.Status,MemoryCaptureStatus.Processing)
                .SetProperty(j=>j.LeaseToken,Guid.NewGuid()).SetProperty(j=>j.LeaseExpiresAt,DateTimeOffset.UtcNow.AddMinutes(5)).SetProperty(j=>j.Attempts,1));
            var invalid="{\"SchemaVersion\":1}";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MemoryEpisodeEnrichmentJobs\" SET \"AcceptedExtractionJson\"=CAST({invalid} AS jsonb),\"ExtractionAcceptedAt\"={DateTimeOffset.UtcNow}");
        }
        var service=ErasureService(fixture,db); var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
        Assert.Equal(held ? "memory_legal_hold_prevents_deletion" : "memory_erasure_generic_lineage_review_required",preview.ApplyBlockedReason);
        Assert.Null(preview.EvidenceToken); Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task EpisodeErasureFencesTheLateProviderResponseAndRemovesItsLease()
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var(user,episode)=await QueueEpisodeErasureAsync(fixture);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider=new ScriptedProviderFactory(async(_,ct)=> { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var worker=fixture.Context(); var processing=fixture.Service(worker,provider).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); await using var db=fixture.Context(); var service=ErasureService(fixture,db);
            var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user);
            Assert.Null(preview.ApplyBlockedReason);
            var result=await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,episode.Id,user,
                new(Guid.NewGuid(),Assert.IsType<string>(preview.EvidenceToken)));
            Assert.Equal(1,result.ClearedJobs); Assert.Empty(await db.MemoryEnrichmentProviderLeases.ToListAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0,await processing.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var check=fixture.Context(); Assert.Empty(await check.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty((await fixture.Store.ExportAsync(episode.Partition)).Claims);
    }
}
