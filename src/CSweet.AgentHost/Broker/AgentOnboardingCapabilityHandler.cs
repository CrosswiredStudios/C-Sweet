using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.AgentHost.Broker;

/// <summary>Acknowledges the stable lifecycle event only from the installation it targets.</summary>
public sealed class AgentOnboardingCapabilityHandler(
    CSweetDbContext db,
    TimeProvider clock,
    ILogger<AgentOnboardingCapabilityHandler>? logger = null) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string capability) =>
        capability == CSweet.Contracts.Agents.AgentLifecycleCapabilities.CompleteOnboarding;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, "The installation identity is invalid.");

        CompleteAgentOnboardingRequest? input;
        try
        {
            input = JsonSerializer.Deserialize<CompleteAgentOnboardingRequest>(request.Payload.Span, JsonOptions);
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, "The onboarding acknowledgement is not valid JSON.");
        }
        if (input is null || input.EventId == Guid.Empty)
            return Failure(request.RequestId, "eventId is required.");

        var agentId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (!agentId.HasValue)
            return Failure(request.RequestId, "The installation is not assigned to an active employee in this organization.");

        var item = await db.AgentOnboardingEventOutbox.SingleOrDefaultAsync(
            x => x.Id == input.EventId && x.OrganizationId == organizationId &&
                 x.AgentOrganizationUserId == agentId.Value,
            cancellationToken);
        if (item is null)
            return Failure(request.RequestId, "The onboarding event was not found for this installation.");

        var completedAt = item.DeliveredAt ?? clock.GetUtcNow();
        if (item.Status != AgentOnboardingEventOutboxStatus.Delivered)
        {
            item.Status = AgentOnboardingEventOutboxStatus.Delivered;
            item.DeliveredAt = completedAt;
            item.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        logger?.LogInformation(
            "Acknowledged onboarding event {OnboardingEventId} for organization {OrganizationId}, employee {AgentOrganizationUserId}, installation {InstallationId}, and conversation {ConversationId} at {CompletedAt}.",
            item.Id,
            item.OrganizationId,
            item.AgentOrganizationUserId,
            installationId,
            item.ConversationId,
            completedAt);

        return Success(request.RequestId, new CompleteAgentOnboardingResponse(true, completedAt));
    }

    private static CapabilityResult Success<T>(string requestId, T value) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    };

    private static CapabilityResult Failure(string requestId, string message) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = message,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(
            new PlatformCapabilityError(PlatformCapabilityErrorCode.Denied, message), JsonOptions))
    };
}
