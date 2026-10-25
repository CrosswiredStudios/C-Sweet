using CSweet.Contracts.Realtime;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class CurrentActivityTests
{
    private static async Task<AgentRunLog> GeneratingRunAsync(Fixture f)
    {
        var id = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var run = await f.Db.AgentRunLogs.SingleAsync(x => x.Id == id);
        run.AgentWorkItemId = f.Attempt.AgentWorkItemId;
        run.GenerationStartedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        run.GenerationObservedAt = run.GenerationStartedAt.Value.AddSeconds(4);
        run.GenerationOutputTokens = 160;
        run.GenerationIsEstimated = false;
        await f.Db.SaveChangesAsync();
        return run;
    }

    [Fact]
    public async Task GenerationIsForTheCurrentResponseAndNeverAnEarlierToolAction()
    {
        var clock = new GenerationTime(DateTimeOffset.UtcNow);
        await using var f = new Fixture(clock); await f.SeedAsync();
        await f.ProgressAsync(f.Ticket.Id, "Running an old tool");
        var run = await GeneratingRunAsync(f);
        // This tool observation occurred before output began.
        run.GenerationStartedAt = DateTimeOffset.UtcNow;
        run.GenerationObservedAt = run.GenerationStartedAt.Value.AddSeconds(4);
        await f.Db.SaveChangesAsync();
        // Use a deterministic service clock after the sample.
        clock.Now = run.GenerationObservedAt.Value.AddSeconds(1);
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.Equal("Generating a response", row.CurrentAction);
        Assert.Equal(run.Id, row.Generation!.ModelRunId);
        Assert.Equal(40d, row.Generation.TokensPerSecond);
        Assert.False(row.Generation.IsEstimated);
        Assert.Contains(row.PreviousSteps, x => x.Kind == "Workspace");

        var next = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        (await f.Db.AgentRunLogs.SingleAsync(x => x.Id == next)).StartedAt = run.GenerationObservedAt.Value;
        await f.Db.SaveChangesAsync();
        Assert.Null(Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneModelCallsCarryTheirOwnMeasurementsWithoutAttachingToAnotherActivity(bool estimated)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await GeneratingRunAsync(f);
        run.AgentWorkItemId = null; run.AgentWorkAttemptId = null; run.WorkItemId = null;
        run.GenerationIsEstimated = estimated;
        await f.Db.SaveChangesAsync();
        var rows = (await f.Service.ReadAsync(f.Org, f.User)).Items;
        var standalone = Assert.Single(rows, x => x.ModelRunId == run.Id);
        Assert.Equal(40d, standalone.Generation!.TokensPerSecond);
        Assert.Equal(estimated, standalone.Generation.IsEstimated);
        Assert.All(rows.Where(x => x.ModelRunId != run.Id), row => Assert.Null(row.Generation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputArrivingDuringTheActivityReadIsNotMistakenForAFutureSample(bool standalone)
    {
        var clock = new GenerationReadTime();
        await using var f = new Fixture(clock); await f.SeedAsync();
        var run = await GeneratingRunAsync(f);
        if (standalone)
        {
            run.AgentWorkItemId = null; run.AgentWorkAttemptId = null; run.WorkItemId = null;
            await f.Db.SaveChangesAsync();
        }
        var observed = run.GenerationObservedAt!.Value;
        clock.RequestStartedAt = observed.AddMilliseconds(-250);
        clock.ProjectedAt = observed.AddMilliseconds(250);
        var page = await f.Service.ReadAsync(f.Org, f.User);
        var row = Assert.Single(page.Items, x => standalone ? x.ModelRunId == run.Id : x.AttemptId == f.Attempt.Id);
        Assert.True(page.CheckedAt < observed);
        Assert.NotNull(row.Generation);
        Assert.Equal(40d, row.Generation.TokensPerSecond);
        Assert.Equal("Generating a response", row.CurrentAction);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("too-short")]
    [InlineData("future")]
    [InlineData("invalid-time")]
    [InlineData("invalid-count")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("queued")]
    [InlineData("lease-expired")]
    [InlineData("diagnostics-denied")]
    [InlineData("other-task")]
    [InlineData("other-attempt")]
    [InlineData("other-installation")]
    [InlineData("other-employee")]
    [InlineData("other-delivery")]
    [InlineData("newer-tool")]
    public async Task GenerationIsHiddenWithoutFreshCurrentAuthorizedOutput(string reason)
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await GeneratingRunAsync(f);
        var observed = run.GenerationObservedAt!.Value;
        switch (reason)
        {
            case "stale": run.GenerationObservedAt = DateTimeOffset.UtcNow.AddSeconds(-16); run.GenerationStartedAt = run.GenerationObservedAt.Value.AddSeconds(-4); break;
            case "too-short": run.GenerationStartedAt = observed.AddMilliseconds(-500); break;
            case "future": run.GenerationObservedAt = DateTimeOffset.UtcNow.AddSeconds(10); break;
            case "invalid-time": run.GenerationStartedAt = observed.AddSeconds(1); break;
            case "invalid-count": run.GenerationOutputTokens = double.PositiveInfinity; break;
            case "completed": run.Status = "Completed"; run.CompletedAt = DateTimeOffset.UtcNow; break;
            case "failed": run.Status = "Failed"; break;
            case "cancelled": run.Status = "Cancelled"; break;
            case "queued": run.Status = "Queued"; break;
            case "lease-expired": f.Attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "diagnostics-denied": f.Owner.PermissionLevel = CSweet.Domain.Core.OrganizationPermissionLevel.Contributor; break;
            case "other-task": run.WorkItemId = Guid.NewGuid(); break;
            case "other-attempt": run.AgentWorkAttemptId = Guid.NewGuid(); break;
            case "other-installation": run.AgentInstallationId = Guid.NewGuid(); break;
            case "other-employee": run.EmployeeId = Guid.NewGuid(); break;
            case "other-delivery": run.AgentWorkItemId = Guid.NewGuid(); break;
            case "newer-tool": await f.ProgressAsync(f.Ticket.Id, "Executing a new tool"); break;
        }
        await f.Db.SaveChangesAsync();
        var rows = (await f.Service.ReadAsync(f.Org, f.User)).Items;
        Assert.All(rows, row => Assert.Null(row.Generation));
    }

    [Fact]
    public async Task TelemetryOnlyWritesDoNotCreateLifecycleEventsAndStreamWakesRemainAtomicAndThrottled()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await GeneratingRunAsync(f);
        var auditCount = await f.Db.AuditOutbox.CountAsync();
        var wakeCount = await f.Db.ApplicationRealtimeOutbox.CountAsync();
        run.GenerationOutputTokens = 200;
        await f.Db.SaveChangesAsync();
        Assert.Equal(auditCount, await f.Db.AuditOutbox.CountAsync());
        Assert.Equal(wakeCount, await f.Db.ApplicationRealtimeOutbox.CountAsync());
        for (var i = 0; i < 2; i++)
        {
            run.GenerationOutputTokens += 20;
            f.Db.QueueAudit(new("model.response.chunk", OrganizationId: f.Org, EntityType: nameof(AgentRunLog),
                EntityId: run.Id, Employees: [new(f.Agent.Id)]));
            await f.Db.SaveChangesAsync();
        }
        Assert.Equal(auditCount + 2, await f.Db.AuditOutbox.CountAsync());
        Assert.Equal(wakeCount + 1, await f.Db.ApplicationRealtimeOutbox.CountAsync());
        Assert.Contains(await f.Db.ApplicationRealtimeOutbox.ToListAsync(), x => x.EventType == AppRealtimeEvents.CurrentActivityChanged);
        Assert.Null(run.ReportedOutputTokens);
        Assert.Null(run.TokenOutputCount);
    }

    private sealed class GenerationTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class GenerationReadTime : TimeProvider
    {
        public DateTimeOffset RequestStartedAt { get; set; }
        public DateTimeOffset ProjectedAt { get; set; }
        private int reads;
        public override DateTimeOffset GetUtcNow() => ++reads == 1 ? RequestStartedAt : ProjectedAt;
    }
}
