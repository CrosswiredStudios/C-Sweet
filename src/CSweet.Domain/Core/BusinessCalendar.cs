namespace CSweet.Domain.Core;

public sealed class BusinessCalendar
{
    public Guid OrganizationId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public long Revision { get; set; } = 1;
}

public sealed class BusinessCalendarEvent
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OwnerOrganizationUserId { get; set; }
    public Guid SchedulingOrganizationUserId { get; set; }
    public Guid? SchedulingInstallationId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string CreationKey { get; set; } = "";
    public long Revision { get; set; } = 1;
    public bool Cancelled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BusinessCalendarException
{
    public Guid EventId { get; set; }
    public DateTime OccurrenceLocal { get; set; }
    public string? PayloadJson { get; set; }
    public bool Cancelled { get; set; }
    public Guid SchedulingOrganizationUserId { get; set; }
    public Guid? SchedulingInstallationId { get; set; }
}

public sealed class BusinessCalendarDispatch
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public DateTime OccurrenceLocal { get; set; }
    public string Kind { get; set; } = "work";
    public Guid RecipientId { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
    public Guid? WorkItemId { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class BusinessCalendarReminder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EventId { get; set; }
    public Guid RecipientId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Read { get; set; }
}

/// <summary>Transactional, provider-neutral changes retained for audit and future sync cursors.</summary>
public sealed class BusinessCalendarChange
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EventId { get; set; }
    public long EventRevision { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
