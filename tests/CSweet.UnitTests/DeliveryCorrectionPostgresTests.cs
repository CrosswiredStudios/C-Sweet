using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class DeliveryCorrectionPostgresTests
{
    [CorrectionPostgresFact]
    public async Task Failed_commit_rolls_back_verdict_findings_handoff_and_outbox_then_restart_recovers_once()
    {
        var settings=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(settings.Host,new[]{"localhost","127.0.0.1","::1"});
        settings.Database="delivery_test_"+Guid.NewGuid().ToString("N");settings.Pooling=false;
        var fault=new FailFindingCommit();
        var options=new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString).AddInterceptors(fault).Options;
        await using var f=new HierarchicalDeliveryServiceTests.Fixture(false,options);
        try
        {
            await f.Db.Database.MigrateAsync();Assert.False(f.Db.Database.HasPendingModelChanges());await f.Seed();
            await HierarchicalTaskRuntimeTests.RunJourney(f,false);
            fault.Armed=true;
            await Assert.ThrowsAsync<InjectedFailure>(()=>DeliveryCorrectionTests.Reject(f,true));
            f.Db.ChangeTracker.Clear();
            Assert.Empty(await f.Db.Set<WorkDeliveryFinding>().ToListAsync());
            Assert.Equal("[]",(await f.Db.WorkDeliveryPlans.SingleAsync()).CorrectionsJson);
            Assert.DoesNotContain(await f.Db.AgentPlatformEventOutbox.ToListAsync(),e=>e.DataJson.Contains("correction.requested"));
            var plan=await f.Read();var release=plan.Executions.Single(e=>e.Scope=="Release");
            Assert.Equal("WaitingForHuman",release.Status);
            var request=f.Review(release);
            await f.Service.CompleteReviewAsync(f.Org,f.Qa.Id,request with {Result=request.Result with {Approved=false,
                Criteria=[new("Requirement works",false,"Wrong sample")],Findings=["Correct the sample coordinate"],Summary="Wrong sample"}});
            f.Db.ChangeTracker.Clear();var accepted=await f.Read();var correction=Assert.Single(accepted.Corrections);
            async Task<bool> Coordinate(string key)
            {
                await using var concurrent=new HierarchicalDeliveryServiceTests.Fixture(false,options){Org=f.Org,Project=f.Project,Manager=f.Manager};
                try {await concurrent.Service.CoordinateCorrectionAsync(f.Org,f.Manager.Id,new(accepted.Id,correction.Id,correction.Revision,key,WaitingReason:"Restore the engineer"));return true;}
                catch(DbUpdateConcurrencyException){return false;}
                catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.SerializationFailure){return false;}
            }
            Assert.Single(await Task.WhenAll(Coordinate("first"),Coordinate("second")),x=>x);
            f.Db.ChangeTracker.Clear();await f.Service.PulseAsync();
            Assert.Single((await f.Read()).Corrections);Assert.Single(await f.Db.Set<WorkDeliveryFinding>().ToListAsync());
        }
        finally
        {
            Assert.StartsWith("delivery_test_",settings.Database);await f.Db.Database.EnsureDeletedAsync();
        }
    }
    private sealed class CorrectionPostgresFactAttribute : FactAttribute
    {
        public CorrectionPostgresFactAttribute()
        { if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"))) Skip="Set CSWEET_COMPUTE_TEST_DATABASE for isolated PostgreSQL tests."; }
    }
    private sealed class InjectedFailure : Exception;
    private sealed class FailFindingCommit : SaveChangesInterceptor
    {
        public bool Armed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken token=default)
        {
            if(Armed&&data.Context!.ChangeTracker.Entries<WorkDeliveryFinding>().Any(e=>e.State==EntityState.Added))
            {Armed=false;throw new InjectedFailure();}
            return ValueTask.FromResult(result);
        }
    }
}
