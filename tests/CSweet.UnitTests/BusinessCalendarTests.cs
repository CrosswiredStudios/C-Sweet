using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.UI.Services;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class BusinessCalendarTests
{
    [Fact]
    public void RecurrencePreservesLocalTimeAcrossDaylightSaving()
    {
        var input = Event(new DateTime(2026, 3, 7, 9, 0, 0)) with { TimeZoneId = "America/Los_Angeles", Recurrence = new("Daily", Count: 3) };
        var starts = CalendarRecurrenceEngine.Starts(input, new DateTime(2026, 3, 10)).Select(x => CalendarRecurrenceEngine.ToInstant(x, input.TimeZoneId)).ToArray();
        Assert.Equal(3, starts.Length);
        Assert.Equal(TimeSpan.FromHours(23), starts[1] - starts[0]);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(starts[1], CalendarRecurrenceEngine.Zone(input.TimeZoneId)).Hour);
    }

    [Fact]
    public void MonthAndLeapYearRulesSkipMissingDates()
    {
        var monthly = Event(new DateTime(2026, 1, 31)) with { Recurrence = new("Monthly", Count: 3) };
        Assert.Equal(new[] { 1, 3, 5 }, CalendarRecurrenceEngine.Starts(monthly, new DateTime(2026, 12, 31)).Select(x => x.Month));
        var yearly = Event(new DateTime(2024, 2, 29)) with { Recurrence = new("Yearly", Count: 2) };
        Assert.Equal(new[] { 2024, 2028 }, CalendarRecurrenceEngine.Starts(yearly, new DateTime(2030, 1, 1)).Select(x => x.Year));
    }

    [Fact]
    public void DstGapAndFoldAreDeterministic()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero), CalendarRecurrenceEngine.ToInstant(new(2026, 3, 8, 2, 30, 0), "America/Los_Angeles"));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero), CalendarRecurrenceEngine.ToInstant(new(2026, 11, 1, 1, 30, 0), "America/Los_Angeles"));
    }

    [Fact]
    public async Task OwnershipViewerAndBusinessBoundariesAreEnforced()
    {
        await using var f = await Fixture.Create();
        var created = await f.Service.CreateAsync(f.Org, f.Actor, new(Event(f.Start), "own"), default);
        var viewer = await f.Member(OrganizationPermissionLevel.Viewer);
        Assert.Single((await f.Service.ReadAsync(f.Org, viewer, f.Query, default)).Occurrences);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CreateAsync(f.Org, viewer, new(Event(f.Start), "denied"), default));
        var peer = await f.Member(OrganizationPermissionLevel.Contributor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.UpdateAsync(f.Org, peer, new(created.Id, 1, Event(f.Start)), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(Guid.NewGuid(), f.Actor, f.Query, default));
        var manager = await f.Member(OrganizationPermissionLevel.Manager);
        var edited = await f.Service.UpdateAsync(f.Org, manager, new(created.Id, 1, Event(f.Start) with { Title = "Manager edit" }), default);
        Assert.Equal(2, edited.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Service.CancelAsync(f.Org, f.Actor, new(created.Id, 1), default));
        Assert.Equal(2, await f.Db.Set<BusinessCalendarChange>().CountAsync());
    }

    [Fact]
    public async Task OccurrenceOverrideAndCancellationDoNotChangeOtherOccurrences()
    {
        await using var f = await Fixture.Create();
        var input = Event(f.Start) with { Recurrence = new("Daily", Count: 3) };
        var e = await f.Service.CreateAsync(f.Org, f.Actor, new(input, "series"), default);
        await f.Service.UpdateAsync(f.Org, f.Actor, new(e.Id, 1, Event(f.Start.AddDays(1).AddHours(2)) with { Title = "Moved" }, f.Start.AddDays(1)), default);
        await f.Service.CancelAsync(f.Org, f.Actor, new(e.Id, 2, f.Start.AddDays(2)), default);
        var read = await f.Service.ReadAsync(f.Org, f.Actor, f.Query, default);
        Assert.Equal(2, read.Occurrences.Count);
        Assert.Equal("Moved", read.Occurrences.Last().Event.Title);
        Assert.Equal(f.Start.AddDays(1), read.Occurrences.Last().OccurrenceLocal);
    }

    [Fact]
    public async Task RemindersAreDurableCoalescedAndPrivateToRecipient()
    {
        await using var f = await Fixture.Create();
        await f.Service.CreateAsync(f.Org, f.Actor, new(Event(f.Start) with { ReminderMinutes = [60, 15, 0] }, "reminder"), default);
        await f.Service.DispatchDueAsync(default);
        await f.Service.DispatchDueAsync(default);
        var reminders = await f.Service.RemindersAsync(f.Org, f.Actor, default);
        Assert.Single(reminders);
        Assert.Equal(2, await f.Db.Set<BusinessCalendarDispatch>().CountAsync(x => x.Status == "Skipped"));
        var peer = await f.Member(OrganizationPermissionLevel.Contributor);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.MarkReadAsync(f.Org, peer, reminders[0].Id, default));
        await f.Service.MarkReadAsync(f.Org, f.Actor, reminders[0].Id, default);
        Assert.Empty(await f.Service.RemindersAsync(f.Org, f.Actor, default));
    }

    [Fact]
    public async Task SchedulingPeerIsDeniedAndLatestMissedWorkCreatesOnlyOneAssignment()
    {
        await using var f = await Fixture.Create();
        var peer = await f.Member(OrganizationPermissionLevel.Contributor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CreateAsync(f.Org, f.Actor,
            new(Event(f.Start) with { Work = new("Instructions", peer.OrganizationUserId!.Value, "Do work") }, "peer"), default));
        var input = Event(f.Start.AddDays(-3)) with { Recurrence = new("Daily", Count: 4), Work = new("Instructions", f.Owner, "Prepare the business report") };
        await f.Service.CreateAsync(f.Org, f.Actor, new(input, "work"), default);
        await f.Service.DispatchDueAsync(default);
        await f.Service.DispatchDueAsync(default);
        Assert.Equal(3, await f.Db.Set<BusinessCalendarDispatch>().CountAsync(x => x.Status == "Skipped"));
        var delivered = await f.Db.Set<BusinessCalendarDispatch>().SingleAsync(x => x.Status != "Skipped");
        Assert.True(delivered.Status == "Delivered", delivered.Error);
        Assert.NotNull(delivered.WorkItemId);
        Assert.Single(await f.Db.CoreWorkTasks.Where(x => x.CreationIdempotencyKey != null && x.CreationIdempotencyKey.StartsWith("calendar:")).ToListAsync());
    }

    [Fact]
    public async Task CreationRetryAfterManagerEditKeepsOriginalEventIdentity()
    {
        await using var f = await Fixture.Create();
        var created = await f.Service.CreateAsync(f.Org, f.Actor, new(Event(f.Start), "stable"), default);
        var manager = await f.Member(OrganizationPermissionLevel.Manager);
        await f.Service.UpdateAsync(f.Org, manager, new(created.Id, 1, Event(f.Start) with { Title = "Edited" }), default);
        var retry = await f.Service.CreateAsync(f.Org, f.Actor, new(Event(f.Start), "stable"), default);
        Assert.Equal(created.Id, retry.Id);
        Assert.Equal("Edited", retry.Event.Title);
        Assert.Single(await f.Db.Set<BusinessCalendarEvent>().ToListAsync());
    }

    [Fact]
    public async Task ExistingWorkActivatesOnceAndCancellationPreventsDispatch()
    {
        await using var f = await Fixture.Create();
        var engine = new WorkItemMutationEngine(f.Db, new FixedClock());
        var item = await engine.AddAsync(f.Org, new(f.Owner, null),
            new AddPersonalTodoItemRequest("Existing", "Prepare report", "Medium", null, "existing") { StartInBacklog = true }, default);
        var input = Event(f.Start) with { Work = new("ExistingItem", f.Owner, ItemId: item.Id) };
        await f.Service.CreateAsync(f.Org, f.Actor, new(input, "activate"), default);
        await f.Service.DispatchDueAsync(default);
        var dispatched = await f.Db.Set<BusinessCalendarDispatch>().SingleAsync();
        Assert.True(dispatched.Status == "Delivered", dispatched.Error);
        Assert.Equal(item.Id, dispatched.WorkItemId);
        await f.Service.DispatchDueAsync(default);
        Assert.Single(await f.Db.CoreWorkTasks.ToListAsync());

        var cancelled = await f.Service.CreateAsync(f.Org, f.Actor,
            new(Event(f.Start) with { Work = new("Instructions", f.Owner, "Cancelled work"), ReminderMinutes = [0] }, "cancel"), default);
        await f.Service.CancelAsync(f.Org, f.Actor, new(cancelled.Id, 1), default);
        await f.Service.DispatchDueAsync(default);
        Assert.False(await f.Db.Set<BusinessCalendarDispatch>().AnyAsync(x => x.EventId == cancelled.Id));
    }

    [Fact]
    public async Task RevokedAgentSchedulingGrantBlocksDueWork()
    {
        await using var f = await Fixture.Create();
        var installation = new CSweet.Domain.Setup.AgentInstallation {
            Id = Guid.NewGuid(), BusinessId = f.Org.ToString(), IsEnabled = true,
            Scope = CSweet.Domain.Setup.PluginInstallationScope.Organization,
            RevisionStatus = CSweet.Domain.Setup.PluginRevisionStatus.Active };
        f.Db.AgentInstallations.Add(installation);
        var owner = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Owner);
        owner.AgentInstallationId = installation.Id; owner.EmployeeType = EmployeeType.Agent;
        var grant = new CSweet.Domain.Setup.AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            RequiredCapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(CalendarCapabilities.All) };
        f.Db.AgentInstallationGrants.Add(grant);
        await f.Db.SaveChangesAsync();
        var actor = new CalendarActor(InstallationId: installation.Id);
        await f.Service.CreateAsync(f.Org, actor,
            new(Event(f.Start) with { Work = new("Instructions", f.Owner, "Report") }, "agent-work"), default);
        grant.RequiredCapabilitiesJson = "[]"; await f.Db.SaveChangesAsync();
        await f.Service.DispatchDueAsync(default);
        var result = await f.Db.Set<BusinessCalendarDispatch>().SingleAsync();
        Assert.Equal("Blocked", result.Status);
        Assert.Contains(CalendarCapabilities.Schedule, result.Error);
        Assert.Empty(await f.Db.CoreWorkTasks.ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(f.Org, actor, f.Query, default));
    }
    [Fact]
    public async Task CeoCanScheduleAnUnattachedWorker()
    {
        await using var f = await Fixture.Create();
        var owner = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Owner);
        owner.PermissionLevel = OrganizationPermissionLevel.Owner;
        await f.Db.SaveChangesAsync();
        var worker = await f.Member(OrganizationPermissionLevel.Contributor);
        await f.Service.CreateAsync(f.Org, f.Actor,
            new(Event(f.Start) with { Work = new("Instructions", worker.OrganizationUserId!.Value, "Prepare report") }, "ceo"), default);
        await f.Service.DispatchDueAsync(default);
        var result = await f.Db.Set<BusinessCalendarDispatch>().SingleAsync();
        Assert.True(result.Status == "Delivered", result.Error);
    }
    [Theory]
    [InlineData("Month")]
    [InlineData("Week")]
    [InlineData("Agenda")]
    public void SwitchingBusinessPreservesCalendarView(string view)
    {
        var target = Guid.NewGuid();
        Assert.Equal($"/organizations/{target}/calendar?view={view}", BusinessNavigation.SwitchDestination($"/organizations/{Guid.NewGuid()}/calendar?view={view}", target));
    }

    [Fact]
    public async Task MovingDispatchedOneTimeEventPreservesAssignmentWithoutReplaying()
    {
        await using var f = await Fixture.Create();
        var input = Event(f.Start) with { Work = new("Instructions", f.Owner, "Report") };
        var created = await f.Service.CreateAsync(f.Org, f.Actor, new(input, "move-once"), default);
        await f.Service.DispatchDueAsync(default);
        var item = (await f.Db.Set<BusinessCalendarDispatch>().SingleAsync()).WorkItemId;
        await f.Service.UpdateAsync(f.Org, f.Actor, new(created.Id, created.Revision,
            input with { StartLocal = f.Start.AddHours(1), EndLocal = f.Start.AddHours(2) }), default);
        await f.Service.DispatchDueAsync(default);
        Assert.Single(await f.Db.CoreWorkTasks.ToListAsync());
        Assert.Equal(item, (await f.Service.ReadAsync(f.Org, f.Actor, f.Query, default)).Occurrences.Single().WorkItemId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DueWorkRechecksReportingAuthorityAndTargetStatus(bool deactivate)
    {
        await using var f = await Fixture.Create();
        var manager = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Owner);
        manager.PermissionLevel = OrganizationPermissionLevel.Manager;
        var workerActor = await f.Member(OrganizationPermissionLevel.Contributor);
        var worker = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.Id == workerActor.OrganizationUserId);
        worker.ReportsToOrganizationUserId = manager.Id;
        await f.Db.SaveChangesAsync();
        await f.Service.CreateAsync(f.Org, f.Actor,
            new(Event(f.Start) with { Work = new("Instructions", worker.Id, "Report") }, "hierarchy"), default);
        if (deactivate) worker.IsActive = false;
        else worker.ReportsToOrganizationUserId = null;
        await f.Db.SaveChangesAsync();
        await f.Service.DispatchDueAsync(default);
        var dispatch = await f.Db.Set<BusinessCalendarDispatch>().SingleAsync();
        Assert.Equal("Blocked", dispatch.Status);
        Assert.False(string.IsNullOrWhiteSpace(dispatch.Error));
        Assert.Empty(await f.Db.CoreWorkTasks.ToListAsync());
    }
    private static CalendarEventInput Event(DateTime start) => new("Planning", start, start.AddHours(1));
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Owner { get; } = Guid.NewGuid();
        public DateTime Start => new(2026, 9, 8, 9, 0, 0);
        public CalendarActor Actor => new(OrganizationUserId: Owner);
        public CalendarQuery Query => new(new DateTimeOffset(Start, TimeSpan.Zero).AddDays(-1), new DateTimeOffset(Start, TimeSpan.Zero).AddDays(7));
        public BusinessCalendarService Service { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Db.CoreOrganizations.Add(new Organization { Id = f.Org, Name = "Calendar business" });
            f.Db.CoreOrganizationUsers.Add(new OrganizationUser { Id = f.Owner, OrganizationId = f.Org, DisplayName = "Owner", PermissionLevel = OrganizationPermissionLevel.Contributor });
            foreach (var action in new[] { "work.personal-todo.add.v1", "work.personal-todo.activate.v1" })
                f.Db.ScopedActionGrants.Add(new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectId = f.Owner,
                    SubjectKind = GrantSubjectKind.OrganizationUser, ScopeKind = GrantScopeKind.Organization, ScopeId = f.Org, Action = action, GrantedAt = DateTimeOffset.UtcNow });
            await f.Db.SaveChangesAsync();
            var clock = new FixedClock();
            f.Service = new(f.Db, clock, new EmployeeHierarchyAccessService(f.Db), new WorkItemMutationEngine(f.Db, clock));
            return f;
        }
        public async Task<CalendarActor> Member(OrganizationPermissionLevel permission)
        {
            var id = Guid.NewGuid(); Db.CoreOrganizationUsers.Add(new OrganizationUser { Id = id, OrganizationId = Org, PermissionLevel = permission, DisplayName = "Worker" });
            await Db.SaveChangesAsync(); return new(OrganizationUserId: id);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
