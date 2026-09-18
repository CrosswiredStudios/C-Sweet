using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    [BenchmarkPostgresFact]
    public async Task InboxRetriesExecutionConflictsAtomicallyAndRevalidatesCancelledLeases()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_EFFICIENCY_TEST_POSTGRES"));
        Assert.StartsWith("efficiency_validation", connection.Database);
        connection.Database = "efficiency_validation_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        var clock = new Clock();
        await using var seed = new CSweetDbContext(options, executionClock: clock);
        try
        {
            await seed.Database.MigrateAsync();
            var org = new Organization { Id = Guid.NewGuid(), Name = "Retry validation" };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/retry" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "test", Version = "1.0.0" };
            var install = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id, BusinessId = org.Id.ToString() };
            var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = install.Id, TickId = Guid.NewGuid() };
            var provider = new LlmProviderProfile { Id = Guid.NewGuid(), Name = "Retry provider" };
            seed.AddRange(org, source, package, install, runtime, provider); await seed.SaveChangesAsync();
            var session = new McpAgentSession { OrganizationId = org.Id.ToString(), AgentInstallationId = install.Id,
                RuntimeInstanceId = runtime.Id };
            const string leaseToken = "test-lease";
            foreach (var operation in new[] { "progress", "renew", "fail", "complete", "cancelled", "queue", "exhausted", "pending", "transaction" })
            {
                var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.Id.ToString(), AgentInstallationId = install.Id,
                    Kind = AgentWorkKind.Capability, Status = AgentWorkStatus.Leased, CreatedAt = clock.Now, AttemptCount = 1,
                    MaximumAttempts = 3, DeadlineAt = clock.Now.AddHours(1), IdempotencyKey = operation };
                var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id,
                    ClaimedAt = clock.Now, LastConfirmedAt = clock.Now, LeaseExpiresAt = clock.Now.AddMinutes(3), Attempt = 1,
                    LeaseTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leaseToken))) };
                seed.AddRange(work, attempt); await seed.SaveChangesAsync();
                await using var stale = new CSweetDbContext(options, executionClock: clock);
                await stale.WorkExecutionContexts.SingleAsync(x => x.Id == attempt.Id);
                await stale.WorkExecutionIntervals.Where(x => x.AgentWorkAttemptId == attempt.Id).LoadAsync();
                await stale.AgentWorkAttempts.Include(x => x.AgentWorkItem).SingleAsync(x => x.Id == attempt.Id);
                // A real committed competing update makes this request's tracked revision stale.
                await using (var winner = new CSweetDbContext(options, executionClock: clock))
                {
                    var winningAttempt = await winner.AgentWorkAttempts.Include(x => x.AgentWorkItem).SingleAsync(x => x.Id == attempt.Id);
                    clock.Now = clock.Now.AddSeconds(1);
                    winningAttempt.LastConfirmedAt = clock.Now;
                    if (operation == "cancelled")
                    {
                        winningAttempt.FinishedAt = clock.Now;
                        winningAttempt.AgentWorkItem!.Status = AgentWorkStatus.Cancelled;
                    }
                    await winner.SaveChangesAsync();
                }
                var inbox = new AgentWorkInbox(stale, new EphemeralDataProtectionProvider(), clock);
                var value = JsonSerializer.SerializeToElement(new { Delta = "Planning the fix" });
                switch (operation)
                {
                    case "progress": await inbox.AppendProgressAsync(session, work.Id, 1, leaseToken, 1, value, default); break;
                    case "renew": await inbox.RenewAsync(session, work.Id, 1, leaseToken, default); break;
                    case "fail": await inbox.FailAsync(session, work.Id, 1, leaseToken, "original failure", default); break;
                    case "complete":
                        await inbox.CompleteAsync(session, work.Id, 1, leaseToken, new(true, value, null), default);
                        // Completion remains idempotent after retry.
                        await inbox.CompleteAsync(session, work.Id, 1, leaseToken, new(true, value, null), default);
                        break;
                    case "cancelled":
                        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                            inbox.AppendProgressAsync(session, work.Id, 1, leaseToken, 1, value, default));
                        break;
                    case "pending":
                        stale.AgentWorkItems.Local.Single().LastError = "caller-owned change";
                        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                            inbox.AppendProgressAsync(session, work.Id, 1, leaseToken, 1, value, default));
                        Assert.Equal("caller-owned change", stale.AgentWorkItems.Local.Single().LastError);
                        break;
                    case "transaction":
                        await using (var transaction = await stale.Database.BeginTransactionAsync())
                        {
                            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                                inbox.AppendProgressAsync(session, work.Id, 1, leaseToken, 1, value, default));
                            await transaction.RollbackAsync();
                        }
                        break;
                    case "queue":
                    case "exhausted":
                        var calls = 0;
                        var receiptId = Guid.NewGuid();
                        async Task SaveReceipt()
                        {
                            calls++;
                            if (operation == "exhausted")
                            {
                                await stale.WorkExecutionContexts.SingleAsync(x => x.Id == attempt.Id);
                                await using var competitor = new CSweetDbContext(options, executionClock: clock);
                                var projection = await competitor.WorkExecutionContexts.SingleAsync(x => x.Id == attempt.Id);
                                projection.Revision++;
                                await competitor.SaveChangesAsync();
                            }
                            stale.AgentRunLogs.Add(new AgentRunLog { Id = receiptId, OrganizationId = org.Id,
                                AgentInstallationId = install.Id, ProviderProfileId = provider.Id, AgentWorkItemId = work.Id,
                                MeasurementKind = "Queue", Status = "Queued", StartedAt = clock.Now });
                            await stale.SaveChangesAsync();
                        }
                        if (operation == "exhausted")
                        {
                            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                                WorkExecutionConflictRetry.RunAsync(stale, SaveReceipt, default));
                            Assert.Equal(3, calls);
                        }
                        else
                        {
                            await WorkExecutionConflictRetry.RunAsync(stale, SaveReceipt, default);
                            Assert.Equal(2, calls);
                        }
                        break;
                }
                await using var check = new CSweetDbContext(options, executionClock: clock);
                var persisted = await check.AgentWorkAttempts.Include(x => x.AgentWorkItem).SingleAsync(x => x.Id == attempt.Id);
                Assert.Equal(operation == "progress" ? 1 : 0,
                    await check.AgentWorkProgress.CountAsync(x => x.AgentWorkAttemptId == attempt.Id));
                Assert.Equal(operation == "progress" ? 1 : 0, persisted.LastProgressSequence);
                Assert.Equal(operation == "queue" ? 1 : 0, await check.AgentRunLogs.CountAsync(x => x.AgentWorkItemId == work.Id));
                Assert.Equal(operation switch { "fail" => AgentWorkStatus.Pending, "complete" => AgentWorkStatus.Completed,
                    "cancelled" => AgentWorkStatus.Cancelled, _ => AgentWorkStatus.Leased }, persisted.AgentWorkItem!.Status);
                if (operation == "fail") Assert.Equal("original failure", persisted.AgentWorkItem!.LastError);
                if (operation == "renew") Assert.Equal(clock.Now.AddMinutes(3), persisted.LeaseExpiresAt);
                // No duplicate interval or failed-save audit outbox entry survives rollback/replay.
                Assert.Single(await check.WorkExecutionIntervals.Where(x => x.AgentWorkAttemptId == attempt.Id).ToListAsync());
                Assert.Equal(operation is "fail" or "complete" or "cancelled" or "queue" ? 0 : 1,
                    await check.WorkExecutionIntervals.CountAsync(x => x.AgentWorkAttemptId == attempt.Id && x.EndedAt == null));
                if (operation == "progress")
                {
                    var progressId = await check.AgentWorkProgress.Where(x => x.AgentWorkAttemptId == attempt.Id).Select(x => x.Id).SingleAsync();
                    Assert.Single(await check.AuditOutbox.Where(x => x.SourceEntityId == progressId).ToListAsync());
                }
            }
        }
        finally { await seed.Database.EnsureDeletedAsync(); }
    }

    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
