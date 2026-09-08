using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.WorkManagement;

public sealed record CalendarActor(Guid? ApplicationUserId = null, Guid? InstallationId = null,
    Guid? OrganizationUserId = null);

public interface IBusinessCalendarService
{
    Task<BusinessCalendarView> ReadAsync(Guid organizationId, CalendarActor actor, CalendarQuery query, CancellationToken token);
    Task<CalendarEventView> CreateAsync(Guid organizationId, CalendarActor actor, CreateCalendarEventRequest request, CancellationToken token);
    Task<CalendarEventView> UpdateAsync(Guid organizationId, CalendarActor actor, UpdateCalendarEventRequest request, CancellationToken token);
    Task<CalendarEventView> CancelAsync(Guid organizationId, CalendarActor actor, CancelCalendarEventRequest request, CancellationToken token);
    Task UpdateSettingsAsync(Guid organizationId, CalendarActor actor, UpdateCalendarSettingsRequest request, CancellationToken token);
    Task<IReadOnlyList<CalendarReminder>> RemindersAsync(Guid organizationId, CalendarActor actor, CancellationToken token);
    Task MarkReadAsync(Guid organizationId, CalendarActor actor, Guid reminderId, CancellationToken token);
    Task DispatchDueAsync(CancellationToken token);
}
