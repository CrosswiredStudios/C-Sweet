using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.IntegrationTests;

public sealed class CalendarPostgresFactAttribute : FactAttribute
{
    public CalendarPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CSWEET_CALENDAR_TEST_POSTGRES")))
            Skip = "Set CSWEET_CALENDAR_TEST_POSTGRES to an isolated PostgreSQL database.";
    }
}

public sealed class CalendarPostgresTests
{
    [CalendarPostgresFact]
    public async Task MigrationAndCompetingSchedulersRecoverWithoutDuplicateAssignments()
    {
        var connection = Environment.GetEnvironmentVariable("CSWEET_CALENDAR_TEST_POSTGRES")!;
        // Never run this destructive test against a business database.
        Assert.StartsWith("calendar_validation", new NpgsqlConnectionStringBuilder(connection).Database);
        DbContextOptions<CSweetDbContext> options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection).Options;
        var org = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var clock = new FixedClock();
        var actor = new CalendarActor(OrganizationUserId: owner);
        BusinessCalendarService Service(CSweetDbContext db) => new(db, clock,
            new EmployeeHierarchyAccessService(db), new WorkItemMutationEngine(db, clock));
        await using (var db = new CSweetDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.CoreOrganizations.Add(new Organization { Id = org, Name = "Isolated calendar test" });
            db.CoreOrganizationUsers.Add(new OrganizationUser { Id = owner, OrganizationId = org,
                DisplayName = "Human", PermissionLevel = OrganizationPermissionLevel.Owner });
            await db.SaveChangesAsync();
            await Service(db).CreateAsync(org, actor, new(new CalendarEventInput("Recovery report",
                new DateTime(2026, 9, 5, 9, 0, 0), new DateTime(2026, 9, 5, 10, 0, 0),
                ReminderMinutes: [0, 15], Recurrence: new("Daily", Count: 4),
                Work: new("Instructions", owner, "Prepare report")), "recovery"), default);
        }

        async Task Dispatch()
        {
            await using var db = new CSweetDbContext(options);
            await Service(db).DispatchDueAsync(default);
        }
        // Separate contexts represent independent scheduler instances and restarts.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Dispatch()));
        await Dispatch();
        await using var verify = new CSweetDbContext(options);
        var eventId = await verify.Set<BusinessCalendarEvent>().Where(x => x.OrganizationId == org).Select(x => x.Id).SingleAsync();
        var dispatches = await verify.Set<BusinessCalendarDispatch>().Where(x => x.EventId == eventId && x.Kind == "work").ToListAsync();
        Assert.Equal(3, dispatches.Count(x => x.Status == "Skipped"));
        var delivered = Assert.Single(dispatches, x => x.Status != "Skipped");
        Assert.True(delivered.Status == "Delivered", delivered.Error);
        Assert.Single(await verify.CoreWorkTasks.Where(x => x.OrganizationId == org).ToListAsync());
        Assert.Single(await verify.Set<BusinessCalendarReminder>().Where(x => x.OrganizationId == org).ToListAsync());
        // The second trigger type activates an existing human backlog item and never reopens it.
        var engine = new WorkItemMutationEngine(verify, clock);
        var backlog = await engine.AddAsync(org, new(owner, null),
            new AddPersonalTodoItemRequest("Backlog", "Report", "Medium", null, "backlog") { StartInBacklog = true }, default);
        var activation = await Service(verify).CreateAsync(org, actor, new(new CalendarEventInput("Activate",
            new DateTime(2026, 9, 8, 9, 0, 0), new DateTime(2026, 9, 8, 10, 0, 0),
            Work: new("ExistingItem", owner, ItemId: backlog.Id)), "activate"), default);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Dispatch()));
        await Dispatch();
        verify.ChangeTracker.Clear();
        var activationRecord = await verify.Set<BusinessCalendarDispatch>().SingleAsync(x => x.EventId == activation.Id);
        Assert.True(activationRecord.Status == "Delivered", activationRecord.Error);
        Assert.Equal(backlog.Id, activationRecord.WorkItemId);

        // A real agent membership gets both canonical work availability and targeted reminders.
        var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = $"https://example.invalid/calendar-test/{org:N}" };
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id,
            AgentId = "calendar-test", Version = "1.0.0", CommitSha = Guid.NewGuid().ToString("N") };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
            PackageVersionId = package.Id, BusinessId = org.ToString() };
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, DisplayName = "Test agent",
            AgentInstallationId = installation.Id, EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor };
        verify.AddRange(source, package, installation, agent);
        await verify.SaveChangesAsync();
        await Service(verify).CreateAsync(org, actor, new(new CalendarEventInput("Agent work",
            new DateTime(2026, 9, 8, 9, 0, 0), new DateTime(2026, 9, 8, 10, 0, 0),
            AttendeeIds: [agent.Id], ReminderMinutes: [0], Work: new("Instructions", agent.Id, "Prepare report")), "agent"), default);
        await Dispatch();
        await Dispatch();
        var outbox = await verify.AgentPlatformEventOutbox.Where(x => x.OrganizationId == org &&
            x.TargetInstallationId == installation.Id).ToListAsync();
        Assert.Single(outbox, x => x.EventType == PersonalTodoEvents.Available);
        Assert.Single(outbox, x => x.EventType == CalendarEvents.ReminderDue);

        // Cancellation and dispatch may race, but after cancellation commits no pending work can run.
        var cancelled = await Service(verify).CreateAsync(org, actor, new(new CalendarEventInput("Cancel race",
            new DateTime(2026, 9, 8, 9, 0, 0), new DateTime(2026, 9, 8, 10, 0, 0),
            Work: new("Instructions", owner, "Cancel race")), "cancel"), default);
        async Task Cancel()
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var db = new CSweetDbContext(options);
                try { await Service(db).CancelAsync(org, actor, new(cancelled.Id, cancelled.Revision), default); return; }
                catch (Exception ex) when (attempt < 5 && Conflict(ex)) { }
            }
        }
        await Task.WhenAll(Cancel(), Dispatch());
        verify.ChangeTracker.Clear();
        Assert.True((await verify.Set<BusinessCalendarEvent>().SingleAsync(x => x.Id == cancelled.Id)).Cancelled);
        var before = await verify.CoreWorkTasks.CountAsync(x => x.OrganizationId == org);
        await Dispatch();
        Assert.Equal(before, await verify.CoreWorkTasks.CountAsync(x => x.OrganizationId == org));
        Assert.InRange(await verify.Set<BusinessCalendarDispatch>().CountAsync(x => x.EventId == cancelled.Id && x.Kind == "work"), 0, 1);
        var editable = await Service(verify).CreateAsync(org, actor, new(new CalendarEventInput("Edit race",
            new DateTime(2026, 9, 10, 9, 0, 0), new DateTime(2026, 9, 10, 10, 0, 0)), "edit"), default);
        async Task<bool> Edit(int index)
        {
            await using var db = new CSweetDbContext(options);
            try
            {
                await Service(db).UpdateAsync(org, actor,
                    new(editable.Id, editable.Revision, editable.Event with { Title = $"Editor {index}" }), default);
                return true;
            }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        var editors = await Task.WhenAll(Enumerable.Range(0, 4).Select(Edit));
        Assert.Single(editors, succeeded => succeeded);
        Assert.Equal(2, await verify.Set<BusinessCalendarChange>().CountAsync(x => x.EventId == editable.Id));
    }

    private static bool Conflict(Exception error) => error is DbUpdateConcurrencyException ||
        error is PostgresException { SqlState: "40001" or "40P01" } ||
        error.InnerException is { } inner && Conflict(inner);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
