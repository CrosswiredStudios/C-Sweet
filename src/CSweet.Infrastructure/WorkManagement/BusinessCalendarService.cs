using System.Data;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class BusinessCalendarService(CSweetDbContext db, TimeProvider clock,
    IEmployeeHierarchyAccessService hierarchy, IPersonalTodoService work) : IBusinessCalendarService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static CalendarEventInput Input(BusinessCalendarEvent e) => Decode<CalendarEventInput>(e.PayloadJson);
    private static bool CanEdit(OrganizationUser actor, BusinessCalendarEvent e) =>
        actor.PermissionLevel >= OrganizationPermissionLevel.Manager ||
        (actor.PermissionLevel >= OrganizationPermissionLevel.Contributor && e.OwnerOrganizationUserId == actor.Id);
    private static CalendarEventView View(BusinessCalendarEvent e, OrganizationUser actor) =>
        new(e.Id, e.Revision, e.OwnerOrganizationUserId, Input(e), e.Cancelled, CanEdit(actor, e));

    private async Task<OrganizationUser> Authorize(Guid org, CalendarActor actor, string capability, CancellationToken token)
    {
        var user = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == org && x.IsActive &&
            (actor.InstallationId != null ? x.AgentInstallationId == actor.InstallationId :
             actor.ApplicationUserId != null ? x.ApplicationUserId == actor.ApplicationUserId :
             actor.OrganizationUserId != null && x.Id == actor.OrganizationUserId), token)
            ?? throw new UnauthorizedAccessException("An active business membership is required.");
        if (actor.InstallationId is { } installationId)
        {
            var grant = await db.AgentInstallationGrants.AsNoTracking().Where(x => x.AgentInstallationId == installationId &&
                x.AgentInstallation!.BusinessId == org.ToString() && x.AgentInstallation.IsEnabled &&
                x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active).SingleOrDefaultAsync(token);
            if (grant is null || !Decode<string[]>(grant.RequiredCapabilitiesJson).Contains(capability, StringComparer.Ordinal))
                throw new UnauthorizedAccessException($"Calendar access requires approval of {capability} in the agent upgrade review.");
        }
        if (capability != CalendarCapabilities.Read && user.PermissionLevel < OrganizationPermissionLevel.Contributor)
            throw new UnauthorizedAccessException("Viewers cannot modify the calendar.");
        return user;
    }

    private async Task ValidateAsync(Guid org, OrganizationUser user, CalendarActor actor, CalendarEventInput input, CancellationToken token)
    {
        CalendarRecurrenceEngine.Validate(input);
        var members = (input.AttendeeIds ?? []).Append(input.OwnerOrganizationUserId ?? user.Id).Distinct().ToArray();
        if (await db.CoreOrganizationUsers.CountAsync(x => x.OrganizationId == org && x.IsActive && members.Contains(x.Id), token) != members.Length)
            throw new ArgumentException("Owners and attendees must be active members of this business.");
        if ((input.OwnerOrganizationUserId ?? user.Id) != user.Id && user.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("Only managers can assign event ownership to another worker.");
        if (input.Work is not { } assignment) return;
        await Authorize(org, actor, CalendarCapabilities.Schedule, token);
        var target = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == assignment.TargetOrganizationUserId && x.OrganizationId == org && x.IsActive, token)
            ?? throw new ArgumentException("The responsible worker is no longer active in this business.");
        if (user.PermissionLevel != OrganizationPermissionLevel.Owner && target.Id != user.Id &&
            (user.PermissionLevel < OrganizationPermissionLevel.Manager || !(await hierarchy.GetSelfAndDescendantsAsync(org, user.Id, token)).Contains(target.Id)))
            throw new UnauthorizedAccessException("Schedule work only for yourself or your reporting descendants.");
        if (assignment.Kind == "ExistingItem")
        {
            var eligible = await (from item in db.CoreWorkTasks
                join board in db.WorkBoards on item.BoardId equals board.Id
                where item.Id == assignment.ItemId && item.OrganizationId == org && board.OrganizationId == org &&
                    board.OwnerOrganizationUserId == target.Id && item.ArchivedAt == null && item.Status == WorkTaskStatus.Backlog
                select item.Id).AnyAsync(token);
            if (!eligible) throw new ArgumentException("Choose an unarchived backlog personal work item belonging to the responsible worker.");
        }
    }

    public async Task<BusinessCalendarView> ReadAsync(Guid org, CalendarActor actor, CalendarQuery query, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Read, token);
        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(366)) throw new ArgumentException("Query up to 366 days at a time.");
        var settings = await db.Set<BusinessCalendar>().AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, token);
        var events = await db.Set<BusinessCalendarEvent>().AsNoTracking().Where(x => x.OrganizationId == org && !x.Cancelled).ToListAsync(token);
        var ids = events.Select(x => x.Id).ToArray();
        var exceptions = await db.Set<BusinessCalendarException>().AsNoTracking().Where(x => ids.Contains(x.EventId)).ToListAsync(token);
        var dispatches = await db.Set<BusinessCalendarDispatch>().AsNoTracking().Where(x => ids.Contains(x.EventId) && x.Kind == "work").ToListAsync(token);
        var workIds = dispatches.Where(x => x.WorkItemId.HasValue).Select(x => x.WorkItemId!.Value).ToArray();
        var workStates = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == org && workIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, x.BlockReason }).ToDictionaryAsync(x => x.Id, token);
        var result = new List<CalendarOccurrence>();
        foreach (var e in events)
        {
            foreach (var occurrence in Expand(e, exceptions.Where(x => x.EventId == e.Id).ToArray(), query.To))
            {
                var input = occurrence.Input;
                var start = CalendarRecurrenceEngine.ToInstant(input.StartLocal, input.TimeZoneId);
                var end = CalendarRecurrenceEngine.ToInstant(input.EndLocal, input.TimeZoneId);
                if (start >= query.To || end <= query.From) continue;
                var dispatch = dispatches.SingleOrDefault(x => x.EventId == e.Id && x.OccurrenceLocal == occurrence.Key);
                if (dispatch == null && Input(e).Recurrence == null)
                    dispatch = dispatches.FirstOrDefault(x => x.EventId == e.Id && x.Status == "Delivered");
                var state = dispatch?.WorkItemId is { } workId ? workStates.GetValueOrDefault(workId) : null;
                result.Add(new(e.Id, occurrence.Key, start, end, View(e, user), input,
                    state?.Status.ToString() ?? dispatch?.Status, state?.BlockReason ?? dispatch?.Error, dispatch?.WorkItemId));
            }
        }
        var members = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == org && x.IsActive)
            .OrderBy(x => x.DisplayName).Select(x => new CalendarMember(x.Id, x.DisplayName)).ToListAsync(token);
        return new(settings?.TimeZoneId ?? "UTC", settings?.Revision ?? 1, user.Id,
            user.PermissionLevel >= OrganizationPermissionLevel.Contributor, user.PermissionLevel >= OrganizationPermissionLevel.Manager,
            members, result.OrderBy(x => x.Start).ToArray());
    }

    private static IEnumerable<(DateTime Key, CalendarEventInput Input, BusinessCalendarException? Exception)> Expand(
        BusinessCalendarEvent e, IReadOnlyList<BusinessCalendarException> exceptions, DateTimeOffset through)
    {
        var input = Input(e);
        var replacements = exceptions.ToDictionary(x => x.OccurrenceLocal);
        var limit = through.UtcDateTime.Year >= 2200 ? new DateTime(2200, 12, 31) : through.UtcDateTime.AddDays(2);
        foreach (var key in CalendarRecurrenceEngine.Starts(input, limit))
        {
            if (replacements.ContainsKey(key)) continue;
            yield return (key, CalendarRecurrenceEngine.At(input, key), null);
        }
        foreach (var exception in exceptions.Where(x => !x.Cancelled && x.PayloadJson != null))
            yield return (exception.OccurrenceLocal, Decode<CalendarEventInput>(exception.PayloadJson!), exception);
    }

    public async Task<CalendarEventView> CreateAsync(Guid org, CalendarActor actor, CreateCalendarEventRequest request, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Create, token);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160) throw new ArgumentException("Supply an idempotency key of up to 160 characters.");
        var creationKey = $"{user.Id:N}:{request.IdempotencyKey}";
        var previous = await db.Set<BusinessCalendarEvent>().SingleOrDefaultAsync(x => x.OrganizationId == org && x.CreationKey == creationKey, token);
        if (previous != null) return View(previous, user);
        await ValidateAsync(org, user, actor, request.Event, token);
        var e = new BusinessCalendarEvent { Id = Guid.NewGuid(), OrganizationId = org,
            OwnerOrganizationUserId = request.Event.OwnerOrganizationUserId ?? user.Id,
            SchedulingOrganizationUserId = user.Id, SchedulingInstallationId = actor.InstallationId,
            PayloadJson = Encode(request.Event with { OwnerOrganizationUserId = request.Event.OwnerOrganizationUserId ?? user.Id }),
            CreationKey = creationKey, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        db.Add(e); Change(e, user.Id, "Created"); await db.SaveChangesAsync(token);
        return View(e, user);
    }

    public async Task<CalendarEventView> UpdateAsync(Guid org, CalendarActor actor, UpdateCalendarEventRequest request, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Update, token);
        await using var transaction = await Transaction(token);
        var e = await Load(org, request.EventId, request.ExpectedRevision, user, token);
        if (e.Cancelled) throw new InvalidOperationException("Cancelled events cannot be edited.");
        ArgumentNullException.ThrowIfNull(request.Event);
        var input = request.Event with { OwnerOrganizationUserId = request.Event.OwnerOrganizationUserId ?? e.OwnerOrganizationUserId };
        // Keeping another worker's ownership is permitted for that event's manager.
        await ValidateAsync(org, user, actor, input, token);
        if (request.OccurrenceLocal is { } occurrence)
        {
            if (input.Recurrence != null || input.OwnerOrganizationUserId != e.OwnerOrganizationUserId)
                throw new ArgumentException("An occurrence cannot change recurrence or series ownership.");
            var exception = await Exception(e, occurrence, token);
            exception.PayloadJson = Encode(input); exception.Cancelled = false;
            exception.SchedulingOrganizationUserId = user.Id; exception.SchedulingInstallationId = actor.InstallationId;
        }
        else
        {
            e.PayloadJson = Encode(input); e.OwnerOrganizationUserId = input.OwnerOrganizationUserId!.Value;
            e.SchedulingOrganizationUserId = user.Id; e.SchedulingInstallationId = actor.InstallationId;
            db.RemoveRange(await db.Set<BusinessCalendarException>().Where(x => x.EventId == e.Id).ToListAsync(token));
        }
        await Invalidate(e.Id, request.OccurrenceLocal, token);
        e.Revision++; e.UpdatedAt = clock.GetUtcNow(); Change(e, user.Id, "Updated", request);
        await db.SaveChangesAsync(token); if (transaction != null) await transaction.CommitAsync(token);
        return View(e, user);
    }

    public async Task<CalendarEventView> CancelAsync(Guid org, CalendarActor actor, CancelCalendarEventRequest request, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Cancel, token);
        await using var transaction = await Transaction(token);
        var e = await Load(org, request.EventId, request.ExpectedRevision, user, token);
        if (request.OccurrenceLocal is { } occurrence) (await Exception(e, occurrence, token)).Cancelled = true;
        else e.Cancelled = true;
        await Invalidate(e.Id, request.OccurrenceLocal, token);
        e.Revision++; e.UpdatedAt = clock.GetUtcNow(); Change(e, user.Id, "Cancelled", request);
        await db.SaveChangesAsync(token); if (transaction != null) await transaction.CommitAsync(token);
        return View(e, user);
    }

    private async Task<BusinessCalendarEvent> Load(Guid org, Guid id, long revision, OrganizationUser user, CancellationToken token)
    {
        var e = await db.Set<BusinessCalendarEvent>().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, token)
            ?? throw new KeyNotFoundException();
        if (!CanEdit(user, e)) throw new UnauthorizedAccessException("Only the event owner, a manager, or the CEO can edit this event.");
        if (e.Revision != revision) throw new DbUpdateConcurrencyException("The event changed. Refresh before saving.");
        return e;
    }

    private async Task<BusinessCalendarException> Exception(BusinessCalendarEvent e, DateTime occurrence, CancellationToken token)
    {
        if (occurrence.Kind != DateTimeKind.Unspecified || Input(e).Recurrence == null ||
            !CalendarRecurrenceEngine.Starts(Input(e), occurrence).Contains(occurrence)) throw new ArgumentException("Unknown recurring occurrence.");
        var item = await db.Set<BusinessCalendarException>().SingleOrDefaultAsync(x => x.EventId == e.Id && x.OccurrenceLocal == occurrence, token);
        if (item != null) return item;
        item = new() { EventId = e.Id, OccurrenceLocal = occurrence }; db.Add(item); return item;
    }

    private async Task Invalidate(Guid eventId, DateTime? occurrence, CancellationToken token)
    {
        var pending = await db.Set<BusinessCalendarDispatch>().Where(x => x.EventId == eventId &&
            (occurrence == null || x.OccurrenceLocal == occurrence) && x.Status != "Delivered" && x.Status != "Skipped").ToListAsync(token);
        // Delivered and skipped occurrence keys are immutable: edits cannot replay already dispatched work.
        db.RemoveRange(pending);
    }

    private void Change(BusinessCalendarEvent e, Guid actor, string action, object? payload = null) => db.Add(new BusinessCalendarChange
    { OrganizationId = e.OrganizationId, EventId = e.Id, EventRevision = e.Revision, ActorId = actor, Action = action,
      PayloadJson = payload is null ? e.PayloadJson : Encode(payload), OccurredAt = clock.GetUtcNow() });

    private async Task<IDbContextTransaction?> Transaction(CancellationToken token) => db.Database.IsRelational()
        ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;

    public async Task UpdateSettingsAsync(Guid org, CalendarActor actor, UpdateCalendarSettingsRequest request, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Update, token);
        if (user.PermissionLevel < OrganizationPermissionLevel.Manager) throw new UnauthorizedAccessException("Only managers can configure the calendar.");
        CalendarRecurrenceEngine.Zone(request.TimeZoneId);
        var settings = await db.Set<BusinessCalendar>().SingleOrDefaultAsync(x => x.OrganizationId == org, token);
        if (settings == null) { settings = new() { OrganizationId = org }; db.Add(settings); }
        if (settings.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Calendar settings changed. Refresh first.");
        settings.TimeZoneId = request.TimeZoneId; settings.Revision++;
        db.Add(new BusinessCalendarChange { OrganizationId = org, EventId = org, EventRevision = settings.Revision,
            ActorId = user.Id, Action = "SettingsUpdated", PayloadJson = Encode(request), OccurredAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<CalendarReminder>> RemindersAsync(Guid org, CalendarActor actor, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Read, token);
        return await db.Set<BusinessCalendarReminder>().AsNoTracking().Where(x => x.OrganizationId == org && x.RecipientId == user.Id && !x.Read)
            .OrderByDescending(x => x.CreatedAt).Take(100).Select(x => new CalendarReminder(x.Id, x.EventId, x.Start, x.Title, x.CreatedAt, x.Read)).ToListAsync(token);
    }

    public async Task MarkReadAsync(Guid org, CalendarActor actor, Guid reminderId, CancellationToken token)
    {
        var user = await Authorize(org, actor, CalendarCapabilities.Read, token);
        var item = await db.Set<BusinessCalendarReminder>().SingleOrDefaultAsync(x => x.Id == reminderId && x.OrganizationId == org && x.RecipientId == user.Id, token)
            ?? throw new KeyNotFoundException();
        item.Read = true; await db.SaveChangesAsync(token);
    }
}
