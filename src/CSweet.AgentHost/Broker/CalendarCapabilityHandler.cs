using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class CalendarCapabilityHandler(IBusinessCalendarService calendar) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => CalendarCapabilities.All.Contains(capability);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CapabilityResult result;
        try
        {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var org) || !Guid.TryParse(session.InstallationId, out var installation))
                throw new UnauthorizedAccessException("Calendar capability is not approved for this installation.");
            var actor = new CalendarActor(InstallationId: installation);
            object response = request.Capability switch
            {
                CalendarCapabilities.Read => await calendar.ReadAsync(org, actor, Read<CalendarQuery>(request), cancellationToken),
                CalendarCapabilities.Create => await calendar.CreateAsync(org, actor, Read<CreateCalendarEventRequest>(request), cancellationToken),
                CalendarCapabilities.Update => await calendar.UpdateAsync(org, actor, Read<UpdateCalendarEventRequest>(request), cancellationToken),
                CalendarCapabilities.Cancel => await calendar.CancelAsync(org, actor, Read<CancelCalendarEventRequest>(request), cancellationToken),
                CalendarCapabilities.Schedule => await calendar.CreateAsync(org, actor, Read<CreateCalendarEventRequest>(request), cancellationToken),
                _ => throw new ArgumentException("Unknown calendar capability.")
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(response, Json)) };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            var code = ex is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied :
                ex is KeyNotFoundException ? PlatformCapabilityErrorCode.NotFound :
                ex is DbUpdateConcurrencyException or InvalidOperationException ? PlatformCapabilityErrorCode.Conflict : PlatformCapabilityErrorCode.ValidationFailed;
            result = new() { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json", Error = ex.Message, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, ex.Message), Json)) };
        }
        yield return result;
    }
    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, Json) ?? throw new ArgumentException("A request payload is required.");
}
