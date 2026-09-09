using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class BusinessCalendarService
{
    public async Task DispatchDueAsync(CancellationToken token)
    {
        var ids = await db.Set<BusinessCalendarEvent>().AsNoTracking().Where(x => !x.Cancelled).Select(x => x.Id).ToListAsync(token);
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            await using var transaction = await Transaction(token);
            try
            {
                // All changes to the event row participate in serializable transactions. Updating its
                // concurrency token also makes dispatch conflict with cancellation and competing schedulers.
                var e = await db.Set<BusinessCalendarEvent>().SingleAsync(x => x.Id == id, token);
                if (e.Cancelled) continue;
                var now = clock.GetUtcNow();
                var exceptions = await db.Set<BusinessCalendarException>().Where(x => x.EventId == id).ToListAsync(token);
                var occurrences = Expand(e, exceptions, now.AddDays(31)).OrderBy(x =>
                    CalendarRecurrenceEngine.ToInstant(x.Input.StartLocal, x.Input.TimeZoneId)).ToArray();
                var records = await db.Set<BusinessCalendarDispatch>().Where(x => x.EventId == id).ToListAsync(token);
                var due = occurrences.Where(x => x.Input.Work != null &&
                    CalendarRecurrenceEngine.ToInstant(x.Input.StartLocal, x.Input.TimeZoneId) <= now).ToArray();
                var touched = false;
                foreach (var occurrence in due)
                {
                    if (records.Any(x => x.Kind == "work" && (x.OccurrenceLocal == occurrence.Key ||
                        (Input(e).Recurrence == null && x.Status == "Delivered")))) continue;
                    var record = new BusinessCalendarDispatch { Id = Guid.NewGuid(), EventId = id,
                        OccurrenceLocal = occurrence.Key, DueAt = CalendarRecurrenceEngine.ToInstant(occurrence.Input.StartLocal, occurrence.Input.TimeZoneId),
                        RecipientId = occurrence.Input.Work!.TargetOrganizationUserId };
                    db.Add(record); records.Add(record); touched = true;
                    if (occurrence.Key != due.Last().Key) { record.Status = "Skipped"; continue; }
                    var actor = new CalendarActor(InstallationId: occurrence.Exception is null ? e.SchedulingInstallationId : occurrence.Exception.SchedulingInstallationId,
                        OrganizationUserId: occurrence.Exception is null ? e.SchedulingOrganizationUserId : occurrence.Exception.SchedulingOrganizationUserId);
                    try
                    {
                        var user = await Authorize(e.OrganizationId, actor, CalendarCapabilities.Schedule, token);
                        await ValidateAsync(e.OrganizationId, user, actor, occurrence.Input, token);
                        var assignment = occurrence.Input.Work!;
                        var key = $"calendar:{id:N}:{occurrence.Key.Ticks}:work";
                        var workActor = new PersonalTodoActor(user.Id, actor.InstallationId);
                        PersonalTodoItem item;
                        if (assignment.Kind == "Instructions")
                        {
                            item = await work.AddAsync(e.OrganizationId, workActor,
                                new AddPersonalTodoItemRequest(occurrence.Input.Title, assignment.Instructions, "Medium",
                                    CalendarRecurrenceEngine.ToInstant(occurrence.Input.EndLocal, occurrence.Input.TimeZoneId), key,
                                    assignment.TargetOrganizationUserId, CorrelationId: id.ToString()), token);
                        }
                        else
                        {
                            var existing = await db.CoreWorkTasks.SingleAsync(x => x.Id == assignment.ItemId && x.OrganizationId == e.OrganizationId, token);
                            item = await work.ActivateAsync(e.OrganizationId, workActor,
                                new ActivatePersonalTodoItemRequest(existing.Id, existing.Revision, key), token);
                        }
                        record.WorkItemId = item.Id; record.Status = "Delivered";
                    }
                    catch (System.Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        record.Status = "Blocked"; record.Error = ex.Message;
                        AddReminder(e, e.OwnerOrganizationUserId, $"Scheduled work blocked: {occurrence.Input.Title}. {ex.Message}", record.DueAt, now);
                    }
                }

                // One reminder per occurrence/recipient/offset. During recovery only the latest due
                // reminder for each recipient is delivered; older keys remain durable skipped records.
                var candidates = occurrences.SelectMany(o => (o.Input.ReminderMinutes ?? []).Distinct()
                    .SelectMany(minutes => (o.Input.AttendeeIds ?? []).Append(e.OwnerOrganizationUserId).Distinct()
                        .Select(recipient => new { Occurrence = o, Minutes = minutes, Recipient = recipient,
                            DueAt = CalendarRecurrenceEngine.ToInstant(o.Input.StartLocal, o.Input.TimeZoneId).AddMinutes(-minutes) })))
                    .Where(x => x.DueAt <= now && !records.Any(r => r.OccurrenceLocal == x.Occurrence.Key &&
                        r.Kind == $"reminder:{x.Minutes}" && r.RecipientId == x.Recipient)).ToArray();
                foreach (var group in candidates.GroupBy(x => x.Recipient))
                {
                    var latest = group.OrderByDescending(x => x.DueAt).First();
                    foreach (var candidate in group)
                    {
                        db.Add(new BusinessCalendarDispatch { Id = Guid.NewGuid(), EventId = id, OccurrenceLocal = candidate.Occurrence.Key,
                            Kind = $"reminder:{candidate.Minutes}", RecipientId = candidate.Recipient, DueAt = candidate.DueAt,
                            Status = ReferenceEquals(candidate, latest) ? "Delivered" : "Skipped" });
                        touched = true;
                    }
                    if (!await db.CoreOrganizationUsers.AnyAsync(x => x.Id == group.Key && x.OrganizationId == e.OrganizationId && x.IsActive, token)) continue;
                    var reminder = AddReminder(e, group.Key, latest.Occurrence.Input.Title,
                        CalendarRecurrenceEngine.ToInstant(latest.Occurrence.Input.StartLocal, latest.Occurrence.Input.TimeZoneId), now);
                    var installation = await db.CoreOrganizationUsers.Where(x => x.Id == group.Key && x.OrganizationId == e.OrganizationId)
                        .Select(x => x.AgentInstallationId).SingleAsync(token);
                    if (installation is { } installationId)
                    {
                        db.Add(new AgentPlatformEventOutboxItem { Id = reminder.Id, OrganizationId = e.OrganizationId,
                            TargetInstallationId = installationId, EventType = CalendarEvents.ReminderDue,
                            DataJson = Encode(new CalendarReminderDueEvent(reminder.Id, id, reminder.Start, reminder.Title, group.Key)),
                            IdempotencyKey = $"calendar-reminder:{reminder.Id:N}", OccurredAt = now, NextAttemptAt = now });
                    }
                }
                if (touched)
                {
                    // Do not increment the public event revision for delivery progress; a separate
                    // concurrency write still participates in the serializable event snapshot.
                    db.Entry(e).Property(x => x.UpdatedAt).IsModified = true;
                    await db.SaveChangesAsync(token);
                }
                if (transaction != null) await transaction.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                if (transaction != null) await transaction.RollbackAsync(token);
                // A competing scheduler or editor won. Retry from a fresh snapshot next pulse.
            }
            finally { db.ChangeTracker.Clear(); }
        }
    }

    private BusinessCalendarReminder AddReminder(BusinessCalendarEvent e, Guid recipient, string title, DateTimeOffset start, DateTimeOffset now)
    {
        var reminder = new BusinessCalendarReminder { Id = Guid.NewGuid(), OrganizationId = e.OrganizationId,
            EventId = e.Id, RecipientId = recipient, Title = title, Start = start, CreatedAt = now };
        db.Add(reminder); return reminder;
    }
}
