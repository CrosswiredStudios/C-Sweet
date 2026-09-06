using CSweet.Application.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;

namespace CSweet.Infrastructure.Communications;

public sealed class AgentCommunicationOnboardingService : IAgentCommunicationOnboardingService
{
    private readonly CSweetDbContext _db;
    private readonly ILogger<AgentCommunicationOnboardingService>? _logger;

    public AgentCommunicationOnboardingService(
        CSweetDbContext db,
        ILogger<AgentCommunicationOnboardingService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AgentCommunicationOnboardingResult> EnsureAsync(
        Guid organizationId,
        OrganizationUser agent,
        Guid? hiringApplicationUserId = null,
        bool queueLifecycleEvent = true,
        CancellationToken cancellationToken = default)
    {
        if (agent.OrganizationId != organizationId || agent.EmployeeType != EmployeeType.Agent || !agent.AgentInstallationId.HasValue)
            return Failure("agent_instance_required", "An active agent instance is required for communication onboarding.");

        var hiringUser = await ResolveHiringUserAsync(organizationId, hiringApplicationUserId, cancellationToken);
        if (hiringUser is null)
            return Failure("hiring_user_not_found", "An active human owner or hiring user is required to start the agent conversation.");

        var existing = _db.CoreConversations.Local.FirstOrDefault(x =>
            x.OrganizationId == organizationId && x.InitiatedByOrganizationUserId == hiringUser.Id &&
            x.AgentOrganizationUserId == agent.Id && x.Kind == ConversationKind.DirectHumanAgent)
            ?? await _db.CoreConversations
                .Include(x => x.Participants)
                .Where(x => x.OrganizationId == organizationId &&
                    x.InitiatedByOrganizationUserId == hiringUser.Id && x.AgentOrganizationUserId == agent.Id &&
                    x.Kind == ConversationKind.DirectHumanAgent)
                .OrderByDescending(x => x.IsDeletionProtected)
                .ThenBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var conversation = existing ?? new Conversation
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            InitiatedByOrganizationUserId = hiringUser.Id,
            AgentOrganizationUserId = agent.Id,
            Kind = ConversationKind.DirectHumanAgent,
            IsPrivate = true,
            IsDeletionProtected = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        conversation.IsPrivate = true;
        conversation.IsDeletionProtected = true;
        conversation.ArchivedAt = null;
        EnsureParticipant(conversation, hiringUser, ConversationParticipantRole.Coordinator, now);
        EnsureParticipant(conversation, agent, ConversationParticipantRole.Member, now);

        AgentOnboardingEventOutboxItem? onboardingEvent = null;
        if (queueLifecycleEvent)
        {
            onboardingEvent = _db.AgentOnboardingEventOutbox.Local.FirstOrDefault(
                    x => x.AgentOrganizationUserId == agent.Id)
                ?? await _db.AgentOnboardingEventOutbox
                    .OrderByDescending(x => x.OccurredAt)
                    .FirstOrDefaultAsync(x => x.AgentOrganizationUserId == agent.Id, cancellationToken);
            if (onboardingEvent is null)
            {
                onboardingEvent = new AgentOnboardingEventOutboxItem
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    AgentOrganizationUserId = agent.Id,
                    HiringOrganizationUserId = hiringUser.Id,
                    ConversationId = conversation.Id,
                    Status = AgentOnboardingEventOutboxStatus.Pending,
                    NextAttemptAt = now,
                    OccurredAt = now
                };
                _db.AgentOnboardingEventOutbox.Add(onboardingEvent);
                _logger?.LogInformation(
                    "Created agent onboarding event {OnboardingEventId} for organization {OrganizationId}, employee {AgentOrganizationUserId}, installation {InstallationId}, hiring employee {HiringOrganizationUserId}, and conversation {ConversationId}.",
                    onboardingEvent.Id,
                    organizationId,
                    agent.Id,
                    agent.AgentInstallationId,
                    hiringUser.Id,
                    conversation.Id);
            }
            else
            {
                _logger?.LogInformation(
                    "Reused agent onboarding event {OnboardingEventId} with status {OnboardingStatus} for organization {OrganizationId}, employee {AgentOrganizationUserId}, installation {InstallationId}, and conversation {ConversationId}.",
                    onboardingEvent.Id,
                    onboardingEvent.Status,
                    organizationId,
                    agent.Id,
                    agent.AgentInstallationId,
                    conversation.Id);
            }
        }

        if (!queueLifecycleEvent)
        {
            var packageJson = await _db.AgentInstallations.AsNoTracking()
                .Where(x => x.Id == agent.AgentInstallationId && x.BusinessId == organizationId.ToString())
                .Select(x => x.PackageVersion!.ManifestJson).SingleOrDefaultAsync(cancellationToken);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(packageJson ?? "{}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is not null && PluginSetupAssistancePolicy.Enabled(manifest))
            {
                var obligation = _db.PluginSetupObligations.Local.FirstOrDefault(x => x.InstallationId == agent.AgentInstallationId)
                    ?? await _db.PluginSetupObligations.SingleOrDefaultAsync(x => x.InstallationId == agent.AgentInstallationId, cancellationToken);
                if (obligation is null)
                    _db.PluginSetupObligations.Add(new PluginSetupObligation
                    {
                        Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = agent.AgentInstallationId.Value,
                        AgentOrganizationUserId = agent.Id, HumanOrganizationUserId = hiringUser.Id,
                        ConversationId = conversation.Id, CreatedAt = now
                    });
                // A later visitor cannot silently replace the designated participant or conversation.
            }
        }
        if (existing is null) _db.CoreConversations.Add(conversation);
        return new AgentCommunicationOnboardingResult(
            true,
            null,
            queueLifecycleEvent
                ? "Agent conversation and lifecycle activation initialized."
                : "Agent conversation initialized while lifecycle activation waits for setup.",
            conversation.Id,
            onboardingEvent?.Id);
    }

    private async Task<OrganizationUser?> ResolveHiringUserAsync(
        Guid organizationId,
        Guid? applicationUserId,
        CancellationToken cancellationToken)
    {
        if (applicationUserId.HasValue)
        {
            var actor = await _db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.ApplicationUserId == applicationUserId && x.EmployeeType == EmployeeType.Human && x.IsActive, cancellationToken);
            if (actor is not null) return actor;
        }

        return await _db.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Human && x.IsActive)
            .OrderByDescending(x => x.PermissionLevel == OrganizationPermissionLevel.Owner)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static void EnsureParticipant(Conversation conversation, OrganizationUser user,
        ConversationParticipantRole role, DateTimeOffset now)
    {
        var participant = conversation.Participants.FirstOrDefault(x => x.OrganizationUserId == user.Id);
        if (participant is null)
            conversation.Participants.Add(new ConversationParticipant
            {
                Id = Guid.NewGuid(), ConversationId = conversation.Id, OrganizationUserId = user.Id,
                OrganizationUser = user, Role = role, JoinedAt = now
            });
        else
        {
            participant.LeftAt = null;
            participant.Role = role;
        }
    }

    private static AgentCommunicationOnboardingResult Failure(string code, string message) => new(false, code, message);
}
