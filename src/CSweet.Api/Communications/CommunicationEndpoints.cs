using CSweet.Api.Auth;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Contracts.Communications;

namespace CSweet.Api.Communications;

public static class CommunicationEndpoints
{
    public static IEndpointRouteBuilder MapCommunicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/communications");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!Guid.TryParse(context.HttpContext.Request.RouteValues["organizationId"]?.ToString(), out var organizationId))
                return Results.NotFound();
            var memory = context.HttpContext.RequestServices.GetRequiredService<IAgentMemoryService>();
            return await memory.CanExploreAsync(organizationId, context.HttpContext.User.GetApplicationUserId(), context.HttpContext.RequestAborted)
                ? await next(context) : Results.Forbid();
        });
        group.MapCommunicationChatTurnEndpoints();

        group.MapGet("/discord", async (Guid organizationId, ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
            await service.GetDiscordAsync(organizationId, cancellationToken) is { } connection ? Results.Ok(connection) : Results.NotFound());

        group.MapGet("/hub", async (Guid organizationId, Guid? perspectiveOrganizationUserId, HttpContext http,
            ICommunicationHubService service, CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            return actorId is null ? Results.Forbid() :
                await service.GetAsync(
                    organizationId,
                    actorId.Value,
                    perspectiveOrganizationUserId,
                    cancellationToken) is { } hub
                    ? Results.Ok(hub) : Results.Forbid();
        });

        group.MapGet("/hub/chats/{chatId:guid}/messages", async (
            Guid organizationId,
            Guid chatId,
            Guid? perspectiveOrganizationUserId,
            HttpContext http,
            ICommunicationHubService service, CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            var messages = await service.ListMessagesAsync(
                organizationId,
                chatId,
                actorId.Value,
                perspectiveOrganizationUserId,
                cancellationToken);
            return messages is null ? Results.NotFound() : Results.Ok(messages);
        });

        group.MapGet("/hub/unread-summary", async (Guid organizationId, HttpContext http,
            ICommunicationHubService service, CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            return await service.GetUnreadSummaryAsync(organizationId, actorId.Value, cancellationToken) is { } summary
                ? Results.Ok(summary) : Results.Forbid();
        });

        group.MapPost("/hub/chats/{chatId:guid}/read", async (Guid organizationId, Guid chatId,
            MarkCommunicationChatReadRequest request, HttpContext http, ICommunicationHubService service,
            CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            return await service.MarkReadAsync(organizationId, chatId, actorId.Value, request.ThroughMessageSequence, cancellationToken)
                is { } summary ? Results.Ok(summary) : Results.NotFound();
        });

        group.MapPost("/hub/chats", async (Guid organizationId, CreateCommunicationChatRequest request, HttpContext http,
            ICommunicationHubService service, CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            var result = await service.CreateAsync(organizationId, actorId.Value, request, cancellationToken);
            return result.Succeeded
                ? Results.Created($"/api/organizations/{organizationId}/communications/hub/chats/{result.Chat!.Id}", result.Chat)
                : HubFailure(result);
        });

        group.MapPut("/hub/chats/{chatId:guid}", async (Guid organizationId, Guid chatId,
            UpdateCommunicationChatRequest request, HttpContext http, ICommunicationHubService service,
            CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            var result = await service.UpdateAsync(organizationId, chatId, actorId.Value, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result.Chat) : HubFailure(result);
        });

        group.MapDelete("/hub/chats/{chatId:guid}", async (Guid organizationId, Guid chatId, HttpContext http,
            ICommunicationHubService service, CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            var result = await service.ArchiveAsync(organizationId, chatId, actorId.Value, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : HubFailure(result);
        });

        group.MapPost("/hub/chats/{chatId:guid}/messages", async (Guid organizationId, Guid chatId,
            SendCommunicationMessageRequest request, HttpContext http, ICommunicationHubService service,
            CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, service, cancellationToken);
            if (actorId is null) return Results.Forbid();
            try
            {
                var result = await service.SendAsync(organizationId, chatId, actorId.Value, request, cancellationToken);
                if (result is null)
                    return Results.BadRequest(new CommunicationHubActionResponse(false, "message_rejected",
                        "The message was empty or you are not an active member of this chat."));
                return result.Turn is null
                    ? Results.Ok(result)
                    : Results.Accepted($"/api/organizations/{organizationId}/communications/hub/chats/{chatId}/turns/{result.Turn.Id}", result);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new CommunicationHubActionResponse(false, "turn_active", exception.Message));
            }
        });

        group.MapPost("/hub/chats/{chatId:guid}/decisions/{decisionId:guid}/respond", async (
            Guid organizationId, Guid chatId, Guid decisionId, AnswerExecutiveDecisionRequest request,
            HttpContext http, ICommunicationHubService hub, IExecutiveDecisionService decisions,
            CancellationToken cancellationToken) =>
        {
            var actorId = await ResolveActorAsync(organizationId, http, hub, cancellationToken);
            if (actorId is null) return Results.Forbid();
            try
            {
                var result = await decisions.AnswerAsync(organizationId, chatId, decisionId,
                    actorId.Value, request, cancellationToken);
                if (result.Succeeded)
                    return result.Turn is null ? Results.Ok(result) : Results.Accepted(
                        $"/api/organizations/{organizationId}/communications/hub/chats/{chatId}/turns/{result.Turn.Id}", result);
                return result.ErrorCode switch
                {
                    "not_authorized" => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
                    "decision_not_found" => Results.NotFound(result),
                    "decision_already_answered" or "decision_not_pending" => Results.Conflict(result),
                    _ => Results.BadRequest(result)
                };
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new AnswerExecutiveDecisionResponse(false, "turn_active", exception.Message));
            }
        });

        group.MapGet("/providers/{providerKey}", async (Guid organizationId, string providerKey,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
            await service.GetAsync(organizationId, providerKey, cancellationToken) is { } connection
                ? Results.Ok(connection) : Results.NotFound());

        group.MapPost("/providers/{providerKey}/connect", async (Guid organizationId, string providerKey,
            ConnectCommunicationWorkspaceRequest request, ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await service.ConnectAsync(organizationId, providerKey, request, cancellationToken)); }
            catch (ArgumentException exception) { return Results.BadRequest(new CommunicationActionResponse(false, "validation_error", exception.Message)); }
        });

        group.MapGet("/providers/{providerKey}/provisioning-preview", async (Guid organizationId, string providerKey,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
        {
            var plan = await service.PreviewAsync(organizationId, providerKey, cancellationToken);
            return plan is null ? Results.NotFound() : Results.Ok(new ProvisioningPreviewResponse(plan.OrganizationId, plan.Provider,
                plan.WorkspaceExternalId, plan.Changes.Select(x => new ProvisioningChangeResponse(x.Change.ToString(), x.Kind.ToString(),
                    x.Purpose, x.DesiredName, x.ExternalId, x.Detail)).ToList(), plan.CreatedAt));
        });

        group.MapPost("/providers/{providerKey}/reconcile", async (Guid organizationId, string providerKey,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
            Results.Accepted(value: await service.QueueReconciliationAsync(organizationId, providerKey, cancellationToken)));

        group.MapDelete("/providers/{providerKey}", async (Guid organizationId, string providerKey,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DisconnectAsync(organizationId, providerKey, cancellationToken);
            return result.Succeeded ? Results.Accepted(value: result) : Results.BadRequest(result);
        });

        group.MapPost("/discord/connect", async (Guid organizationId, ConnectDiscordWorkspaceRequest request,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await service.ConnectDiscordAsync(organizationId, request, cancellationToken)); }
            catch (ArgumentException exception) { return Results.BadRequest(new CommunicationActionResponse(false, "validation_error", exception.Message)); }
        });

        group.MapGet("/discord/provisioning-preview", async (Guid organizationId, ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
        {
            var plan = await service.PreviewAsync(organizationId, cancellationToken);
            return plan is null ? Results.NotFound() : Results.Ok(new ProvisioningPreviewResponse(plan.OrganizationId, plan.Provider,
                plan.WorkspaceExternalId, plan.Changes.Select(x => new ProvisioningChangeResponse(x.Change.ToString(), x.Kind.ToString(),
                    x.Purpose, x.DesiredName, x.ExternalId, x.Detail)).ToList(), plan.CreatedAt));
        });

        group.MapPost("/discord/reconcile", async (Guid organizationId, ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
            Results.Accepted(value: await service.QueueReconciliationAsync(organizationId, cancellationToken)));

        group.MapDelete("/discord", async (Guid organizationId, ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DisconnectDiscordAsync(organizationId, cancellationToken);
            return result.Succeeded ? Results.Accepted(value: result) : Results.BadRequest(result);
        });

        group.MapPost("/discord/link-code", async (Guid organizationId, HttpContext http,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
            await service.CreateLinkCodeAsync(organizationId, http.User.GetApplicationUserId()!.Value, cancellationToken) is { } code
                ? Results.Ok(code) : Results.NotFound());

        group.MapPost("/discord/direct-agent", async (Guid organizationId, SelectDirectAgentRequest request, HttpContext http,
            ICommunicationWorkspaceService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SelectDirectAgentAsync(organizationId, http.User.GetApplicationUserId()!.Value, request.AgentOrganizationUserId, cancellationToken)));

        group.MapGet("/notifications", async (Guid organizationId, HttpContext http, INotificationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(organizationId, http.User.GetApplicationUserId()!.Value, cancellationToken)));
        group.MapPost("/notifications/{notificationId:guid}/read", async (Guid organizationId, Guid notificationId, HttpContext http,
            INotificationService service, CancellationToken cancellationToken) =>
            await service.MarkReadAsync(organizationId, http.User.GetApplicationUserId()!.Value, notificationId, cancellationToken) ? Results.NoContent() : Results.NotFound());
        return endpoints;
    }

    private static async Task<Guid?> ResolveActorAsync(Guid organizationId, HttpContext http,
        ICommunicationHubService service, CancellationToken cancellationToken)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        return applicationUserId.HasValue
            ? await service.ResolveOrganizationUserIdAsync(organizationId, applicationUserId.Value, cancellationToken)
            : null;
    }

    private static IResult HubFailure(CommunicationHubActionResponse result) => result.ErrorCode switch
    {
        "not_authorized" => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
        "chat_not_found" or "actor_not_found" => Results.NotFound(result),
        _ => Results.BadRequest(result)
    };
}
