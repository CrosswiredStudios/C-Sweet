using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.IntegrationTests;

public sealed class WorkExecutionPostgresTests
{
    [BenchmarkPostgresFact]
    public async Task SqlClaimPlanReportsConcurrencyAndRollbackCommitTimingAtomically()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_EFFICIENCY_TEST_POSTGRES"));
        Assert.StartsWith("efficiency_validation", connection.Database);
        connection.Database = "efficiency_validation_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        var clock = new Clock();
        await using var db = new CSweetDbContext(options, executionClock: clock);
        try
        {
            await db.Database.MigrateAsync();
            var org = new Organization { Id = Guid.NewGuid(), Name = "Timing validation" };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/timing" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "test", Version = "1.0.0" };
            var install = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id, BusinessId = org.Id.ToString() };
            var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, EmployeeType = EmployeeType.Agent,
                DisplayName = "Developer", AgentInstallationId = install.Id, IsActive = true };
            var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = install.Id, TickId = Guid.NewGuid() };
            var provider = new LlmProviderProfile { Id = Guid.NewGuid(), Name = "Validation provider" };
            db.AddRange(org, source, package, install, owner, runtime, provider); await db.SaveChangesAsync();
            var service = new PersonalTodoService(db, clock); var actor = new PersonalTodoActor(owner.Id, install.Id);
            var root = await service.AddAsync(org.Id, actor, new("Build", null, Wire.WorkPriorities.Medium, null, "add", null));
            var ready = root;
            var eventId = Guid.NewGuid();
            var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.Id.ToString(), AgentInstallationId = install.Id,
                Kind = AgentWorkKind.Event, SourceId = eventId.ToString(), Status = AgentWorkStatus.Leased, CreatedAt = clock.Now,
                DeadlineAt = clock.Now.AddHours(1), IdempotencyKey = "work" };
            var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id,
                ClaimedAt = clock.Now, LastConfirmedAt = clock.Now, LeaseExpiresAt = clock.Now.AddHours(1), Attempt = 1, LeaseTokenHash = "test" };
            db.AddRange(work, attempt); await db.SaveChangesAsync();
            var claim = await service.ClaimAsync(org.Id, actor, new(eventId, "claim") { ItemId = root.Id, ExpectedRevision = ready.Revision });
            Assert.NotNull(claim.Item);
            Assert.Single(await db.WorkLifecycleEvents.Where(x => x.ResourceId == root.Id && x.Status == "Running").ToListAsync());
            Assert.Equal(root.Id, (await db.WorkExecutionContexts.SingleAsync()).WorkItemId);
            var plan = await service.CreatePlanAsync(org.Id, actor, new(root.Id, "Build epic",
            [new("implementation", "Implementation", "Implement", ["Done"],
                [new("first", "First", "Build", ["Pass"]), new("second", "Second", "Build more", ["Pass"])]),
             new("story", "Story", "Scope", ["Done"],
                [new("validate", "Validate", "Checks", ["Pass"], "Validation"),
                 new("deploy", "Deploy", "Release", ["Live"], "Deployment")])], "plan"));
            var tasks = plan.Items.Where(x => x.Kind == "Task").OrderBy(x => x.Rank).ToList();
            clock.Now = clock.Now.AddMinutes(1);
            var started = await service.ReportPlanTaskAsync(org.Id, actor, new(root.Id, tasks[0].Id, tasks[0].Revision, "Running", null, "start"));
            var call = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = org.Id, AgentInstallationId = install.Id,
                ProviderProfileId = provider.Id, StartedAt = clock.Now, ProviderStartedAt = clock.Now, MeasurementKind = "ProviderAttempt",
                Status = "Completed", ReportedInputTokens = 100, ReportedOutputTokens = 25 };
            await InferenceAttribution.CaptureAsync(db, call, work.Id, attemptNumber: 1); db.Add(call); await db.SaveChangesAsync();
            Assert.Equal(tasks[0].Id, call.WorkItemId);
            clock.Now = clock.Now.AddMinutes(1);
            await service.ReportPlanTaskAsync(org.Id, actor, new(root.Id, tasks[0].Id, started.Revision, "Completed", "Passed", "finish"));
            var before = await db.WorkExecutionIntervals.CountAsync();
            await service.ReportPlanTaskAsync(org.Id, actor, new(root.Id, tasks[0].Id, started.Revision, "Completed", "Passed", "finish"));
            Assert.Equal(before, await db.WorkExecutionIntervals.CountAsync());

            var wakesBefore = await db.ApplicationRealtimeOutbox.CountAsync();
            var auditBefore = await db.AuditOutbox.CountAsync();
            // A rolled-back plan transition must not publish focus, timing, lifecycle or wakes.
            await using (var rollback = new CSweetDbContext(options, executionClock: clock))
            {
                await using var tx = await rollback.Database.BeginTransactionAsync();
                await new PersonalTodoService(rollback, clock).ReportPlanTaskAsync(org.Id, actor,
                    new(root.Id, tasks[1].Id, tasks[1].Revision, "Running", null, "rollback"));
                await tx.RollbackAsync();
            }
            await using (var check = new CSweetDbContext(options, executionClock: clock))
            {
                Assert.Equal(root.Id, (await check.WorkExecutionContexts.SingleAsync()).WorkItemId);
                Assert.Equal(before, await check.WorkExecutionIntervals.CountAsync());
                Assert.Equal(wakesBefore, await check.ApplicationRealtimeOutbox.CountAsync());
                Assert.Equal(auditBefore, await check.AuditOutbox.CountAsync());
                Assert.False(await check.WorkLifecycleEvents.AnyAsync(x => x.ResourceId == tasks[1].Id && x.Status == "Running"));
            }
            // Competing saves based on the same context revision cannot each publish a focus.
            await using var first = new CSweetDbContext(options, executionClock: clock);
            await using var second = new CSweetDbContext(options, executionClock: clock);
            await first.WorkExecutionContexts.SingleAsync(); await second.WorkExecutionContexts.SingleAsync();
            var a = await first.AgentWorkAttempts.SingleAsync(); var b = await second.AgentWorkAttempts.SingleAsync();
            clock.Now = clock.Now.AddSeconds(10); a.LastConfirmedAt = b.LastConfirmedAt = clock.Now;
            await first.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            var report = await new WorkEfficiencyService(first, clock).GetAsync(org.Id);
            Assert.Equal(125, report.WorkItems.Single(x => x.Id == root.Id).Total.TotalTokens);
            Assert.Equal(60_000, report.WorkItems.Single(x => x.Id == tasks[0].Id).Direct.ActiveAgentTimeMs);
            Assert.Single((await new WorkEfficiencyService(first, clock).GetActivityAsync(org.Id, root.Id, null, 0, 25, subtree: true)).Calls);
            clock.Now = clock.Now.AddHours(2);
            await using var recovery = new CSweetDbContext(options, executionClock: clock);
            Assert.Equal(1, await WorkExecutionRecoveryWorker.RecoverAsync(recovery, clock.Now));
            Assert.Equal(0, await WorkExecutionRecoveryWorker.RecoverAsync(recovery, clock.Now));
            Assert.Equal(report.BusinessTotal.ActiveAgentTimeMs,
                WorkEfficiencyService.Effort(await recovery.WorkExecutionIntervals.ToListAsync(), null, null, clock.Now));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
