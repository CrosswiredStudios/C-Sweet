using System.Text.Json;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Components.Employees;

/// <summary>Decides whether a realtime app event should refresh a personal board view.</summary>
public static class PersonalBoardRealtime
{
    public static bool Matches(AppRealtimeEventEnvelope envelope, Guid organizationId, Guid boardId) =>
        envelope.OrganizationId == organizationId &&
        envelope.EventType == AppRealtimeEvents.WorkBoardChanged &&
        envelope.Data.ValueKind == JsonValueKind.Object &&
        envelope.Data.TryGetProperty("boardId", out var boardIdElement) &&
        boardIdElement.TryGetGuid(out var eventBoardId) &&
        eventBoardId == boardId;
}
