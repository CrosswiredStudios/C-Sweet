using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuggestUserActionRequest = CSweet.Contracts.Communications.SuggestUserActionRequest;

namespace CSweet.AgentHost.Broker;

public sealed class CommunicationHubCapabilityHandler(
    CSweetDbContext db,
    ICommunicationHubService hub,
    IExecutiveDecisionService? decisions = null,
    IUserActionService? userActions = null,
    ILogger<CommunicationHubCapabilityHandler>? logger = null) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string capability) =>
        CommunicationHubCapabilities.All.Contains(capability) ||
        capability == SuggestedUserActionCapabilities.Suggest;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken token)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                $"The installation is not granted {request.Capability}.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The installation identity is invalid.");

        var actorId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(token);
        if (!actorId.HasValue)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The installation is not assigned to an active employee in this organization.");

        try
        {
            return request.Capability switch
            {
                CommunicationHubCapabilities.Read => await ReadAsync(request, organizationId, actorId.Value, token),
                CommunicationHubCapabilities.Create => FromAction(request.RequestId,
                    await hub.CreateAsync(organizationId, actorId.Value, Read<CreateCommunicationChatRequest>(request), token)),
                CommunicationHubCapabilities.Modify => await ModifyAsync(request, organizationId, actorId.Value, token),
                CommunicationHubCapabilities.Delete => FromAction(request.RequestId,
                    await hub.ArchiveAsync(organizationId,
                        Read<ChatReference>(request).ChatId ?? throw new JsonException("chatId is required."),
                        actorId.Value, token)),
                CommunicationHubCapabilities.SendMessage => await SendAsync(request, organizationId, actorId.Value, token),
                CommunicationHubCapabilities.AskUser => await AskUserAsync(
                    request, organizationId, installationId, token),
                SuggestedUserActionCapabilities.Suggest => Success(request.RequestId,
                    await (userActions ?? throw new InvalidOperationException("The suggested action service is unavailable."))
                        .SuggestAsync(organizationId, installationId, Read<SuggestUserActionRequest>(request), token)),
                _ => Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, "The communication capability is not implemented.")
            };
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed,
                "The capability payload is not valid JSON.");
        }
        catch (ArgumentException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
    }

    private async Task<CapabilityResult> ReadAsync(RequestCapability request, Guid organizationId, Guid actorId, CancellationToken token)
    {
        var input = request.Payload.IsEmpty ? new ChatReference(null) : Read<ChatReference>(request);
        if (input.ChatId.HasValue)
        {
            var messages = await hub.ListMessagesAsync(
                organizationId,
                input.ChatId.Value,
                actorId,
                cancellationToken: token);
            return messages is null
                ? Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, "The chat was not found or is not visible to this employee.")
                : Success(request.RequestId, new CommunicationChatMessagesResponse(messages));
        }
        var response = await hub.GetAsync(
            organizationId,
            actorId,
            cancellationToken: token);
        return response is null
            ? Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, "The communication hub was not found.")
            : Success(request.RequestId, response);
    }

    private async Task<CapabilityResult> ModifyAsync(RequestCapability request, Guid organizationId, Guid actorId, CancellationToken token)
    {
        var input = Read<ModifyChatCapabilityRequest>(request);
        var update = new UpdateCommunicationChatRequest(input.Title, input.Description, input.IsPrivate,
            input.ParticipantOrganizationUserIds, input.AudienceRoleIds, input.AudienceWorkstreamIds);
        return FromAction(request.RequestId, await hub.UpdateAsync(organizationId, input.ChatId, actorId, update, token));
    }

    private async Task<CapabilityResult> SendAsync(RequestCapability request, Guid organizationId, Guid actorId, CancellationToken token)
    {
        var input = Read<SendMessageCapabilityRequest>(request);
        var message = await hub.SendAsync(organizationId, input.ChatId, actorId,
            new SendCommunicationMessageRequest(
                input.Content, input.IdempotencyKey, input.Mentions, input.AttachmentMediaAssetIds), token);
        if (message is null)
        {
            logger?.LogWarning(
                "Agent communication send failed for organization {OrganizationId}, employee {AgentOrganizationUserId}, chat {ConversationId}, request {RequestId}, and idempotency key {IdempotencyKey}. The message was empty or the employee is not an active chat member.",
                organizationId,
                actorId,
                input.ChatId,
                request.RequestId,
                input.IdempotencyKey);
        }
        else
        {
            logger?.LogInformation(
                "Agent communication message {MessageId} persisted for organization {OrganizationId}, employee {AgentOrganizationUserId}, chat {ConversationId}, request {RequestId}, and idempotency key {IdempotencyKey}.",
                message.Message.Id,
                organizationId,
                actorId,
                input.ChatId,
                request.RequestId,
                input.IdempotencyKey);
        }
        return message is null
            ? Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed,
                "The message was empty or the employee is not a member of the chat.")
            : Success(request.RequestId, message.Message);
    }

    private async Task<CapabilityResult> AskUserAsync(
        RequestCapability request,
        Guid organizationId,
        Guid installationId,
        CancellationToken token)
    {
        var input = Read<AskUserCapabilityRequest>(request);
        try
        {
            var decision = await (decisions ?? throw new InvalidOperationException("The executive decision service is unavailable.")).CreateAsync(new CreateExecutiveDecisionCommand(
                organizationId, input.ConversationId, input.ChatTurnId, input.ConversationMessageId, installationId, input.Prompt,
                input.Options.Select(x => new CSweet.Application.Communications.CreateExecutiveDecisionOption(x.Id, x.Label, x.Description)).ToList(),
                input.RecommendedOptionId, input.IdempotencyKey), token);
            return Success(request.RequestId, decision);
        }
        catch (ArgumentException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
    }

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new JsonException("The payload was empty.");

    private static CapabilityResult FromAction(string requestId, CommunicationHubActionResponse action) =>
        action.Succeeded ? Success(requestId, action) : Failure(requestId,
            action.ErrorCode is "not_authorized" ? PlatformCapabilityErrorCode.Denied : PlatformCapabilityErrorCode.ValidationFailed,
            action.Message);

    private static CapabilityResult Success<T>(string requestId, T payload) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
    };

    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string message) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = message,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, message), JsonOptions))
    };

    private sealed record ChatReference(Guid? ChatId);
    private sealed record ModifyChatCapabilityRequest(
        Guid ChatId, string Title, string? Description, bool IsPrivate,
        IReadOnlyList<Guid> ParticipantOrganizationUserIds,
        IReadOnlyList<Guid>? AudienceRoleIds = null,
        IReadOnlyList<Guid>? AudienceWorkstreamIds = null);
    private sealed record SendMessageCapabilityRequest(
        Guid ChatId,
        string Content,
        string? IdempotencyKey = null,
        IReadOnlyList<CSweet.Contracts.Communications.CommunicationMessageMentionInput>? Mentions = null,
        IReadOnlyList<Guid>? AttachmentMediaAssetIds = null);
    private sealed record AskUserCapabilityRequest(
        Guid ConversationId,
        Guid? ChatTurnId,
        Guid? ConversationMessageId,
        string Prompt,
        IReadOnlyList<CreateDecisionOptionCapabilityRequest> Options,
        string RecommendedOptionId,
        string IdempotencyKey);
    private sealed record CreateDecisionOptionCapabilityRequest(string Id, string Label, string? Description = null);
}
