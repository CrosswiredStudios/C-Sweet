using System.Text.Json;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;

namespace CSweet.Infrastructure.PersonalAssistant;

internal static class PersonalAssistantRealtime
{
    public static void Queue(CSweetDbContext db, Guid userId, string eventType, Guid resourceId)
    {
        var now = DateTimeOffset.UtcNow;
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(),
            RecipientApplicationUserId = userId,
            EventType = eventType,
            Subject = $"personal-assistant/{resourceId:D}",
            DataJson = JsonSerializer.Serialize(new { resourceId }),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }
}
