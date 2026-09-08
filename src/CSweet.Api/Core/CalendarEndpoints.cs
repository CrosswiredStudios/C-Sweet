using CSweet.Api.Auth;
using CSweet.Application.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/calendar").RequireAuthorization();
        group.MapGet("", (Guid organizationId, DateTimeOffset from, DateTimeOffset to, HttpContext http,
            IBusinessCalendarService service, CancellationToken token) => Execute(http, async actor =>
                Results.Ok(await service.ReadAsync(organizationId, actor, new(from, to), token))));
        group.MapPost("/events", (Guid organizationId, CreateCalendarEventRequest request, HttpContext http,
            IBusinessCalendarService service, CancellationToken token) => Execute(http, async actor =>
                Results.Ok(await service.CreateAsync(organizationId, actor, request, token))));
        group.MapPut("/events", (Guid organizationId, UpdateCalendarEventRequest request, HttpContext http,
            IBusinessCalendarService service, CancellationToken token) => Execute(http, async actor =>
                Results.Ok(await service.UpdateAsync(organizationId, actor, request, token))));
        group.MapPost("/events/cancel", (Guid organizationId, CancelCalendarEventRequest request, HttpContext http,
            IBusinessCalendarService service, CancellationToken token) => Execute(http, async actor =>
                Results.Ok(await service.CancelAsync(organizationId, actor, request, token))));
        group.MapPut("/settings", (Guid organizationId, UpdateCalendarSettingsRequest request, HttpContext http,
            IBusinessCalendarService service, CancellationToken token) => Execute(http, async actor =>
            { await service.UpdateSettingsAsync(organizationId, actor, request, token); return Results.NoContent(); }));
        group.MapGet("/reminders", (Guid organizationId, HttpContext http, IBusinessCalendarService service, CancellationToken token) =>
            Execute(http, async actor => Results.Ok(await service.RemindersAsync(organizationId, actor, token))));
        group.MapPost("/reminders/{reminderId:guid}/read", (Guid organizationId, Guid reminderId, HttpContext http,
            IBusinessCalendarService service, CancellationToken token) => Execute(http, async actor =>
            { await service.MarkReadAsync(organizationId, actor, reminderId, token); return Results.NoContent(); }));
        return endpoints;
    }

    private static async Task<IResult> Execute(HttpContext http, Func<CalendarActor, Task<IResult>> action)
    {
        var user = http.User.GetApplicationUserId();
        if (user is null) return Results.Unauthorized();
        try { return await action(new CalendarActor(ApplicationUserId: user.Value)); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { message = ex.Message }, statusCode: 403); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (DbUpdateConcurrencyException ex) { return Results.Conflict(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
    }
}
