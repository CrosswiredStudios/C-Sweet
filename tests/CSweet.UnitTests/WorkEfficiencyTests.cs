using System.Text.Json;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkEfficiencyTests
{
    internal static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
    internal sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    internal static AgentRunLog Call(Guid organization, long? input = 10, long? output = 5) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organization, AgentKey = "test", InvocationKind = "llm",
        MeasurementKind = "ProviderAttempt", ProviderStartedAt = Now.AddMinutes(-1), StartedAt = Now.AddMinutes(-1),
        CompletedAt = Now, Status = "Completed", ReportedInputTokens = input, ReportedOutputTokens = output,
        DurationMs = 60_000
    };

    [Fact]
    public async Task ProviderTotals_ExcludeQueueAndPreDispatchFailures_KeepUnknownAndLongUsage()
    {
        await using var db = Database(); var org = Guid.NewGuid();
        var good = Call(org, 3_000_000_000, 20); good.TokenCachedInputCount = 100; good.TokenReasoningCount = 10;
        var failed = Call(org, null, null); failed.Status = "Failed";
        var cancelled = Call(org, 4, null); cancelled.Status = "Cancelled";
        var queue = Call(org); queue.InvocationKind = "llm-queue";
        var notDispatched = Call(org); notDispatched.ProviderStartedAt = null; notDispatched.Status = "Failed";
        db.AgentRunLogs.AddRange(good, failed, cancelled, queue, notDispatched); await db.SaveChangesAsync();
        var totals = await db.AgentRunLogs.ProviderCalls().SumAsync(default);
        Assert.Equal(3, totals.ModelCalls); Assert.Equal(3_000_000_024, totals.TotalTokens);
        Assert.Equal(1, totals.FullyReportedCalls); Assert.Equal(1, totals.FailedCalls); Assert.Equal(1, totals.CancelledCalls);
        Assert.Equal(100, totals.CachedInputTokens); Assert.Equal(10, totals.ReasoningTokens);
        Assert.Equal(totals, WorkEfficiencyService.Sum(await db.AgentRunLogs.ProviderCalls().ToListAsync()));
    }

    [Fact]
    public async Task Rollups_UseCapturedAncestorsAndProject_AfterReparenting_AndScopeTenant()
    {
        await using var db = Database(); var org = Guid.NewGuid(); var oldProject = Guid.NewGuid(); var newProject = Guid.NewGuid();
        var epic = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, Title = "Original epic", Kind = WorkItemKind.Epic, CreatedAt = Now };
        var other = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, Title = "New epic", Kind = WorkItemKind.Epic, CreatedAt = Now };
        var child = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, ParentWorkTaskId = other.Id, Title = "Moved task", CreatedAt = Now };
        db.CoreWorkTasks.AddRange(epic, other, child);
        db.Workstreams.AddRange(new Workstream { Id = oldProject, OrganizationId = org, Name = "Original", CreatedAt = Now },
            new Workstream { Id = newProject, OrganizationId = org, Name = "New", CreatedAt = Now });
        var call = Call(org); call.WorkItemId = child.Id; call.WorkstreamId = oldProject; call.AttributionKind = "Execution";
        call.AncestorWorkItemIdsJson = JsonSerializer.Serialize(new[] { epic.Id, epic.Id });
        db.AgentRunLogs.AddRange(call, Call(Guid.NewGuid(), 999, 999)); await db.SaveChangesAsync();
        var result = await new WorkEfficiencyService(db, new Clock(Now)).GetAsync(org);
        Assert.Equal(1, result.BusinessTotal.ModelCalls);
        Assert.Equal(1, result.WorkItems.Single(x => x.Id == child.Id).Direct.ModelCalls);
        Assert.Equal(1, result.WorkItems.Single(x => x.Id == epic.Id).Total.ModelCalls);
        Assert.Equal(0, result.WorkItems.Single(x => x.Id == other.Id).Total.ModelCalls);
        Assert.Equal(1, result.Projects.Single(x => x.Id == oldProject).Total.ModelCalls);
        Assert.Equal(0, result.Projects.Single(x => x.Id == newProject).Total.ModelCalls);
    }

    [Fact]
    public async Task Lifecycle_RepeatedTransitionsArePreserved_AndHistoryCannotBeEdited()
    {
        await using var db = Database();
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Title = "Task", CreatedAt = Now, UpdatedAt = Now };
        db.CoreWorkTasks.Add(item); await db.SaveChangesAsync();
        foreach (var status in new[] { WorkTaskStatus.Running, WorkTaskStatus.Completed, WorkTaskStatus.Running, WorkTaskStatus.Completed })
        { item.Status = status; await db.SaveChangesAsync(); }
        Assert.Equal(5, await db.WorkLifecycleEvents.CountAsync());
        var history = await db.WorkLifecycleEvents.ToListAsync(); history[0].Status = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Lifecycle_WaitsAndReopening_DoNotSumParallelTaskDurations()
    {
        var states = new[] { "Backlog", "Running", "Blocked", "Completed", "Running", "Completed" };
        var events = states.Select((status, i) => new WorkLifecycleEvent { Status = status,
            PreviousStatus = i == 0 ? null : states[i - 1], OccurredAt = Now.AddHours(i) }).ToList();
        var value = WorkEfficiencyService.Lifecycle(events, Now, Now.AddHours(10));
        Assert.True(value.CompleteHistory); Assert.Equal(1, value.ReopenCount);
        Assert.Equal(5 * 3_600_000, value.LeadTimeMs); Assert.Equal(4 * 3_600_000, value.CycleTimeMs);
        Assert.Equal(3_600_000, value.WaitingTimeMs);
        Assert.Null(WorkEfficiencyService.Lifecycle(events.Take(5), Now, Now.AddHours(10)).CompletedAt);
        Assert.False(WorkEfficiencyService.Lifecycle(events.Skip(1), Now, Now.AddHours(10)).CompleteHistory);
    }

    [Fact]
    public async Task Activity_IsTenantScoped_Paged_AndDoesNotExposeRequestPayloads()
    {
        await using var db = Database(); var org = Guid.NewGuid();
        db.AgentRunLogs.AddRange(Call(org), Call(org), Call(Guid.NewGuid())); await db.SaveChangesAsync();
        var service = new WorkEfficiencyService(db, new Clock(Now));
        var page = await service.GetActivityAsync(org, null, null, 0, 1);
        Assert.Single(page.Calls); Assert.True(page.HasMore);
        var second = await service.GetActivityAsync(org, null, null, 1, 1);
        Assert.Single(second.Calls); Assert.False(second.HasMore); Assert.NotEqual(page.Calls[0].Id, second.Calls[0].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(org, Now, Now));
    }
}
