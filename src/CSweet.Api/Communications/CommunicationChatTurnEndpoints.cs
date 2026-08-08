using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Api.Chat;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Communications;

internal static class CommunicationChatTurnEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapCommunicationChatTurnEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/hub/chats/{chatId:guid}/turns", ListAsync);
        group.MapGet("/hub/chats/{chatId:guid}/turns/{turnId:guid}", GetAsync);
        group.MapGet("/hub/chats/{chatId:guid}/turns/{turnId:guid}/trace", TraceAsync);
        group.MapGet("/hub/chats/{chatId:guid}/turns/{turnId:guid}/events", StreamEventsAsync);
        group.MapPost("/hub/chats/{chatId:guid}/turns/{turnId:guid}/retry", RetryAsync);
        group.MapPost("/hub/chats/{chatId:guid}/turns/{turnId:guid}/cancel", CancelAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId, Guid chatId, HttpContext http,
        ICommunicationHubService hub, IChatTurnService turns, CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(organizationId, chatId, http, hub, cancellationToken)) return Results.Forbid();
        return Results.Ok(await turns.ListForConversationAsync(organizationId, chatId, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId, Guid chatId, Guid turnId, HttpContext http,
        ICommunicationHubService hub, IChatTurnService turns, CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(organizationId, chatId, http, hub, cancellationToken)) return Results.Forbid();
        var turn = await turns.GetAsync(organizationId, turnId, cancellationToken);
        return turn is null || turn.ConversationId != chatId ? Results.NotFound() : Results.Ok(turn);
    }

    private static async Task<IResult> TraceAsync(
        Guid organizationId, Guid chatId, Guid turnId, long? afterSequence, HttpContext http,
        ICommunicationHubService hub, IChatTurnService turns, CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(organizationId, chatId, http, hub, cancellationToken)) return Results.Forbid();
        var turn = await turns.GetAsync(organizationId, turnId, cancellationToken);
        return turn is null || turn.ConversationId != chatId
            ? Results.NotFound()
            : Results.Ok(await turns.ListEventsAsync(organizationId, turnId, afterSequence ?? -1, cancellationToken));
    }

    private static async Task<IResult> RetryAsync(
        Guid organizationId, Guid chatId, Guid turnId, HttpContext http,
        ICommunicationHubService hub, IChatTurnService turns, CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(organizationId, chatId, http, hub, cancellationToken)) return Results.Forbid();
        var original = await turns.GetAsync(organizationId, turnId, cancellationToken);
        if (original is null || original.ConversationId != chatId) return Results.NotFound();
        try
        {
            var result = await turns.RetryAsync(organizationId, turnId, cancellationToken);
            return result is null
                ? Results.Conflict(new { error = "Only failed, cancelled, or warning turns can be retried." })
                : Results.Accepted($"/api/organizations/{organizationId}/communications/hub/chats/{chatId}/turns/{result.Turn.Id}", result);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> CancelAsync(
        Guid organizationId, Guid chatId, Guid turnId, HttpContext http,
        ICommunicationHubService hub, IChatTurnService turns, IChatTurnEventRouter router,
        IChatStreamRouter streamRouter, AgentWorkInbox inbox,
        CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(organizationId, chatId, http, hub, cancellationToken)) return Results.Forbid();
        var snapshot = await turns.GetAsync(organizationId, turnId, cancellationToken);
        if (snapshot is null || snapshot.ConversationId != chatId || IsTerminal(snapshot.Status)) return Results.NotFound();
        var traceEvent = await turns.TraceAsync(turnId, "system", "turn.cancelled", "cancelled", "Turn cancelled",
            "The user cancelled this turn.", cancellationToken: cancellationToken);
        router.Publish(traceEvent);
        if (!await turns.CancelAsync(organizationId, turnId, cancellationToken)) return Results.NotFound();
        await inbox.CancelBySourceAsync(
            "chat-turn",
            turnId.ToString("D"),
            "The user stopped this response.",
            cancellationToken);
        streamRouter.Complete(turnId);
        streamRouter.UnbindAlias(chatId, turnId);
        return Results.Ok(await turns.GetAsync(organizationId, turnId, cancellationToken));
    }

    private static async Task StreamEventsAsync(
        Guid organizationId, Guid chatId, Guid turnId, long? afterSequence, HttpContext http,
        ICommunicationHubService hub, IChatTurnService turns, IChatTurnEventRouter router,
        IOptions<ChatTurnOptions> options, CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(organizationId, chatId, http, hub, cancellationToken))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var snapshot = await turns.GetAsync(organizationId, turnId, cancellationToken);
        if (snapshot is null || snapshot.ConversationId != chatId)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.StartAsync(cancellationToken);

        var cursor = afterSequence ?? ParseLastEventId(http.Request.Headers["Last-Event-ID"].FirstOrDefault());
        var reader = router.Subscribe(turnId);
        foreach (var traceEvent in await turns.ListEventsAsync(organizationId, turnId, cursor, cancellationToken))
        {
            await WriteAsync(http, traceEvent, cancellationToken);
            cursor = traceEvent.Sequence;
        }
        snapshot = await turns.GetAsync(organizationId, turnId, cancellationToken);
        if (snapshot is null || IsTerminal(snapshot.Status)) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            var wait = reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(wait, Task.Delay(options.Value.StreamHeartbeatInterval, cancellationToken));
            if (completed != wait)
            {
                await http.Response.WriteAsync(": heartbeat\n\n", cancellationToken);
                await http.Response.Body.FlushAsync(cancellationToken);
                snapshot = await turns.GetAsync(organizationId, turnId, cancellationToken);
                if (snapshot is null || IsTerminal(snapshot.Status)) return;
                continue;
            }
            if (!await wait) return;
            while (reader.TryRead(out var traceEvent))
            {
                if (traceEvent.Sequence <= cursor) continue;
                await WriteAsync(http, traceEvent, cancellationToken);
                cursor = traceEvent.Sequence;
            }
        }
    }

    private static async Task<bool> CanAccessAsync(
        Guid organizationId, Guid chatId, HttpContext http,
        ICommunicationHubService hub, CancellationToken cancellationToken)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return false;
        var actorId = await hub.ResolveOrganizationUserIdAsync(organizationId, applicationUserId.Value, cancellationToken);
        return actorId.HasValue && await hub.CanAccessChatAsync(organizationId, chatId, actorId.Value, cancellationToken);
    }

    private static async Task WriteAsync(HttpContext http, ChatTurnTraceEventResponse traceEvent, CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync($"id: {traceEvent.Sequence}\nevent: trace\ndata: {JsonSerializer.Serialize(traceEvent, JsonOptions)}\n\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }

    private static long ParseLastEventId(string? value) => long.TryParse(value, out var parsed) ? parsed : -1;
    private static bool IsTerminal(string status) => status is "Completed" or "CompletedWithWarnings" or "Failed" or "Cancelled";
}
