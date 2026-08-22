using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using DomainSession = CSweet.Domain.Communications.AgentCoordinationSession;
using DomainStatus = CSweet.Domain.Communications.AgentCoordinationStatus;
using DomainTurn = CSweet.Domain.Communications.AgentCoordinationTurn;

namespace CSweet.Infrastructure.Communications;

public sealed class AgentCoordinationService(
    CSweetDbContext db,
    ICommunicationHubService hub,
    AgentWorkInbox inbox) : IAgentCoordinationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan TurnDeadline = TimeSpan.FromHours(1);

    public async Task<AgentCoordinationSession> StartAsync(
        Guid organizationId,
        Guid initiatorOrganizationUserId,
        Guid initiatorInstallationId,
        StartAgentCoordinationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateStart(request);
        var existing = await QuerySession().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
            return await MapAsync(existing, cancellationToken);

        var source = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.SourceChatTurnId && x.OrganizationId == organizationId &&
            x.ConversationId == request.SourceConversationId &&
            x.UserMessageId == request.SourceMessageId &&
            x.TargetAgentOrganizationUserId == initiatorOrganizationUserId,
            cancellationToken) ?? throw new InvalidOperationException(
            "The source chat turn does not belong to the initiating agent.");

        var targetInstallationId = await ResolveParticipantsAsync(
            organizationId, initiatorOrganizationUserId, initiatorInstallationId,
            request.TargetOrganizationUserId, cancellationToken);
        var chatAction = await hub.CreateAsync(
            organizationId,
            initiatorOrganizationUserId,
            new CreateCommunicationChatRequest(
                null,
                $"Agent collaboration: {request.Subject.Trim()}",
                true,
                true,
                [request.TargetOrganizationUserId]),
            cancellationToken);
        var chat = chatAction.Chat ?? throw new InvalidOperationException(chatAction.Message);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var session = new DomainSession
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ConversationId = chat.Id,
            SourceConversationId = request.SourceConversationId,
            SourceChatTurnId = request.SourceChatTurnId,
            SourceMessageId = request.SourceMessageId,
            InitiatorOrganizationUserId = initiatorOrganizationUserId,
            InitiatorInstallationId = initiatorInstallationId,
            TargetOrganizationUserId = request.TargetOrganizationUserId,
            TargetInstallationId = targetInstallationId,
            CurrentOrganizationUserId = request.TargetOrganizationUserId,
            Subject = request.Subject.Trim(),
            Objective = request.Objective.Trim(),
            SuccessCriteriaJson = JsonSerializer.Serialize(
                request.SuccessCriteria.Select(x => x.Trim()).ToArray(), JsonOptions),
            Status = DomainStatus.Active,
            Revision = 1,
            NextTurnOrdinal = 1,
            IdempotencyKey = request.IdempotencyKey.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        var initialMessage = AppendMessage(session, initiatorOrganizationUserId,
            request.InitialMessage.Trim(), $"coordination:{session.Id:N}:initial");
        var initialTurn = new DomainTurn
        {
            Id = Guid.NewGuid(), SessionId = session.Id, EventId = Guid.NewGuid(),
            SpeakerOrganizationUserId = initiatorOrganizationUserId,
            ConversationMessageId = initialMessage.Id,
            Ordinal = 0, Disposition = AgentCoordinationDispositions.Continue,
            Content = request.InitialMessage.Trim(),
            IdempotencyKey = $"coordination:{session.Id:N}:initial", CreatedAt = now
        };
        ApplyArtifact(initialTurn, request.Artifact);
        session.Turns.Add(initialTurn);
        db.AgentCoordinationSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        session.CurrentAgentWorkItemId = await EnqueueTurnAsync(session, targetInstallationId,
            request.TargetOrganizationUserId, cancellationToken);
        initialTurn.AgentWorkItemId = session.CurrentAgentWorkItemId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    public async Task<AgentCoordinationSession> RespondAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid actorInstallationId,
        RespondToAgentCoordinationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateResponse(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await QuerySession().SingleOrDefaultAsync(x =>
            x.Id == request.SessionId && x.OrganizationId == organizationId,
            cancellationToken) ?? throw new KeyNotFoundException("The coordination session was not found.");
        var duplicate = session.Turns.SingleOrDefault(x => x.IdempotencyKey == request.IdempotencyKey);
        if (duplicate is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await MapAsync(session, cancellationToken);
        }
        if (session.Status is not (DomainStatus.Active or DomainStatus.Summarizing))
            throw new InvalidOperationException($"The coordination session is {session.Status}.");
        if (session.Revision != request.ExpectedRevision ||
            session.NextTurnOrdinal != request.ExpectedTurnOrdinal)
            throw new InvalidOperationException("The coordination response is stale or out of order.");
        if (session.CurrentOrganizationUserId != actorOrganizationUserId ||
            InstallationFor(session, actorOrganizationUserId) != actorInstallationId)
            throw new UnauthorizedAccessException("This agent does not own the current coordination turn.");
        if (request.Disposition == AgentCoordinationDispositions.Continue && session.IsFinalization)
            throw new InvalidOperationException("A finalization turn cannot continue the session.");
        if (request.Disposition == AgentCoordinationDispositions.Continue && session.Turns.Any(x =>
                string.Equals(x.Content.Trim(), request.Content.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A coordination turn cannot repeat an earlier message exactly.");

        var now = DateTimeOffset.UtcNow;
        var message = AppendMessage(session, actorOrganizationUserId, request.Content.Trim(),
            request.IdempotencyKey.Trim());
        var responseTurn = new DomainTurn
        {
            Id = Guid.NewGuid(), SessionId = session.Id, EventId = Guid.NewGuid(),
            SpeakerOrganizationUserId = actorOrganizationUserId,
            ConversationMessageId = message.Id,
            Ordinal = request.ExpectedTurnOrdinal,
            Disposition = request.Disposition,
            Content = request.Content.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim(),
            CreatedAt = now
        };
        ApplyArtifact(responseTurn, request.Artifact);
        session.Turns.Add(responseTurn);
        db.AgentCoordinationTurns.Add(responseTurn);
        session.Revision++;
        session.NextTurnOrdinal++;
        session.UpdatedAt = now;
        session.CurrentAgentWorkItemId = null;

        if (session.IsFinalization)
        {
            var terminal = session.Turns.Where(x => x.Ordinal < request.ExpectedTurnOrdinal)
                .OrderByDescending(x => x.Ordinal).First();
            session.Status = terminal.Disposition == AgentCoordinationDispositions.Blocked
                ? DomainStatus.Blocked : DomainStatus.Completed;
            session.FinalSummary = request.Content.Trim();
            session.CurrentOrganizationUserId = null;
            session.CompletedAt = now;
            AppendSourceSummary(session, request.Content.Trim());
        }
        else if (request.Disposition == AgentCoordinationDispositions.Continue)
        {
            var nextUserId = OtherParticipant(session, actorOrganizationUserId);
            session.CurrentOrganizationUserId = nextUserId;
            var nextInstallationId = InstallationFor(session, nextUserId);
            await db.SaveChangesAsync(cancellationToken);
            session.CurrentAgentWorkItemId = await EnqueueTurnAsync(
                session, nextInstallationId, nextUserId, cancellationToken);
            responseTurn.AgentWorkItemId = session.CurrentAgentWorkItemId;
        }
        else if (actorOrganizationUserId == session.InitiatorOrganizationUserId)
        {
            session.Status = request.Disposition == AgentCoordinationDispositions.Blocked
                ? DomainStatus.Blocked : DomainStatus.Completed;
            session.FinalSummary = request.Content.Trim();
            session.CurrentOrganizationUserId = null;
            session.CompletedAt = now;
            AppendSourceSummary(session, request.Content.Trim());
        }
        else
        {
            session.Status = DomainStatus.Summarizing;
            session.IsFinalization = true;
            session.CurrentOrganizationUserId = session.InitiatorOrganizationUserId;
            await db.SaveChangesAsync(cancellationToken);
            session.CurrentAgentWorkItemId = await EnqueueTurnAsync(
                session, session.InitiatorInstallationId,
                session.InitiatorOrganizationUserId, cancellationToken);
            responseTurn.AgentWorkItemId = session.CurrentAgentWorkItemId;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    public async Task<AgentCoordinationSession?> ReadAsync(
        Guid organizationId, Guid actorOrganizationUserId, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await QuerySession().SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.OrganizationId == organizationId &&
            (x.InitiatorOrganizationUserId == actorOrganizationUserId ||
             x.TargetOrganizationUserId == actorOrganizationUserId), cancellationToken);
        return session is null ? null : await MapAsync(session, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCoordinationSession>> ListAsync(
        Guid organizationId, Guid actorOrganizationUserId, Guid? chatId, bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        var actorIsActive = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
            x.Id == actorOrganizationUserId && x.OrganizationId == organizationId && x.IsActive,
            cancellationToken);
        if (!actorIsActive) return [];
        var query = QuerySession().Where(x => x.OrganizationId == organizationId &&
            (x.InitiatorOrganizationUserId == actorOrganizationUserId ||
             x.TargetOrganizationUserId == actorOrganizationUserId));
        if (chatId.HasValue)
            query = query.Where(x => x.ConversationId == chatId || x.SourceConversationId == chatId);
        if (activeOnly)
            query = query.Where(x => x.Status == DomainStatus.Active || x.Status == DomainStatus.Summarizing);
        var sessions = await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(cancellationToken);
        var mapped = new List<AgentCoordinationSession>(sessions.Count);
        foreach (var session in sessions) mapped.Add(await MapAsync(session, cancellationToken));
        return mapped;
    }

    public async Task<AgentCoordinationSession> ResumeAsync(
        Guid organizationId,
        Guid actorOrganizationUserId,
        Guid actorInstallationId,
        ResumeAgentCoordinationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Coordination session, recovery reason, and idempotency key are required.");
        if (request.Reason.Length > 2048 || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("The coordination recovery payload is too long.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await QuerySession().SingleOrDefaultAsync(x =>
            x.Id == request.SessionId && x.OrganizationId == organizationId,
            cancellationToken) ?? throw new KeyNotFoundException("The coordination session was not found.");
        if (string.Equals(session.LastResumeIdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await MapAsync(session, cancellationToken);
        }
        if (session.InitiatorOrganizationUserId != actorOrganizationUserId ||
            session.InitiatorInstallationId != actorInstallationId)
            throw new UnauthorizedAccessException("Only the initiating agent may resume this coordination session.");
        if (session.Revision != request.ExpectedRevision)
            throw new InvalidOperationException("The coordination session changed before it could be resumed.");
        if (session.Status is not (DomainStatus.Failed or DomainStatus.Blocked))
            throw new InvalidOperationException($"The coordination session is {session.Status} and cannot be resumed.");

        var now = DateTimeOffset.UtcNow;
        session.Status = DomainStatus.Active;
        session.Revision++;
        session.IsFinalization = false;
        session.CurrentOrganizationUserId = session.InitiatorOrganizationUserId;
        session.CurrentAgentWorkItemId = null;
        session.CompletedAt = null;
        session.FinalSummary = null;
        session.LastResumeIdempotencyKey = request.IdempotencyKey.Trim();
        session.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        session.CurrentAgentWorkItemId = await EnqueueTurnAsync(
            session, session.InitiatorInstallationId,
            session.InitiatorOrganizationUserId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    public async Task<AgentCoordinationSession> CancelAsync(
        Guid organizationId, Guid actorOrganizationUserId, bool actorCanManage,
        CancelAgentCoordinationRequest request,
        CancellationToken cancellationToken = default)
    {
        var isParticipant = await db.AgentCoordinationSessions.AsNoTracking().AnyAsync(x =>
            x.Id == request.SessionId && x.OrganizationId == organizationId &&
            (x.InitiatorOrganizationUserId == actorOrganizationUserId ||
             x.TargetOrganizationUserId == actorOrganizationUserId), cancellationToken);
        if (!actorCanManage && !isParticipant)
            throw new UnauthorizedAccessException("Only a coordination participant or an organization owner or manager may stop agent collaboration.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A cancellation reason is required.");
        var session = await QuerySession().SingleOrDefaultAsync(x =>
            x.Id == request.SessionId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("The coordination session was not found.");
        if (session.Status == DomainStatus.Cancelled &&
            session.FinalSummary?.Contains(request.Reason.Trim(), StringComparison.Ordinal) == true)
            return await MapAsync(session, cancellationToken);
        if (session.Revision != request.ExpectedRevision)
            throw new InvalidOperationException("The coordination session changed before it could be cancelled.");
        if (session.Status is not (DomainStatus.Active or DomainStatus.Summarizing))
            return await MapAsync(session, cancellationToken);
        session.Status = DomainStatus.Cancelled;
        session.Revision++;
        session.CurrentOrganizationUserId = null;
        session.CompletedAt = session.UpdatedAt = DateTimeOffset.UtcNow;
        session.FinalSummary = $"Collaboration was stopped by an authorized user: {request.Reason.Trim()}";
        if (session.CurrentAgentWorkItemId.HasValue)
            await inbox.CancelAsync(session.CurrentAgentWorkItemId.Value,
                "The coordination session was cancelled.", cancellationToken);
        session.CurrentAgentWorkItemId = null;
        AppendSourceSummary(session, session.FinalSummary);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    private async Task<Guid> EnqueueTurnAsync(
        DomainSession session, Guid installationId, Guid currentUserId,
        CancellationToken cancellationToken)
    {
        var request = await BuildTurnRequestAsync(session, currentUserId, cancellationToken);
        var eventId = Guid.NewGuid();
        var work = await inbox.EnqueueAsync(
            session.OrganizationId.ToString("D"),
            installationId,
            CSweet.Domain.Setup.AgentWorkKind.Event,
            AgentCoordinationEvents.TurnRequested,
            JsonSerializer.SerializeToElement(request, JsonOptions),
            $"coordination:{session.Id:N}:turn:{session.NextTurnOrdinal}",
            DateTimeOffset.UtcNow.Add(TurnDeadline),
            correlationId: session.Id.ToString("D"),
            causationId: session.Turns.OrderByDescending(x => x.Ordinal).First().Id.ToString("D"),
            sourceType: "agent-coordination",
            sourceId: eventId.ToString("D"),
            maximumAttempts: 3,
            cancellationToken: cancellationToken);
        return work.Id;
    }

    private async Task<AgentCoordinationTurnRequest> BuildTurnRequestAsync(
        DomainSession session, Guid currentUserId, CancellationToken cancellationToken)
    {
        var mapped = await MapAsync(session, cancellationToken);
        var self = mapped.Initiator.OrganizationUserId == currentUserId ? mapped.Initiator : mapped.Target;
        var other = mapped.Initiator.OrganizationUserId == currentUserId ? mapped.Target : mapped.Initiator;
        return new AgentCoordinationTurnRequest(
            session.Id, session.Revision, session.NextTurnOrdinal,
            session.Subject, session.Objective, mapped.SuccessCriteria,
            self, other, session.IsFinalization, mapped.Turns);
    }

    private ConversationMessage AppendMessage(
        DomainSession session, Guid senderId, string content, string idempotencyKey)
    {
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = session.ConversationId,
            CoordinationSessionId = session.Id, SenderOrganizationUserId = senderId,
            Role = ConversationRole.Assistant, Content = content,
            CorrelationId = session.Id, DeliveryIntent = CommunicationDeliveryIntent.Inform,
            SourceProvider = "InApp", IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreConversationMessages.Add(message);
        return message;
    }

    private void AppendSourceSummary(DomainSession session, string summary)
    {
        if (session.SourceConversationId == session.ConversationId)
            return;

        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = session.SourceConversationId,
            CoordinationSessionId = session.Id,
            SenderOrganizationUserId = session.InitiatorOrganizationUserId,
            Role = ConversationRole.Assistant, Content = summary,
            CorrelationId = session.Id, DeliveryIntent = CommunicationDeliveryIntent.Response,
            SourceProvider = "InApp", IdempotencyKey = $"coordination:{session.Id:N}:summary",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task<Guid> ResolveParticipantsAsync(
        Guid organizationId, Guid initiatorUserId, Guid initiatorInstallationId,
        Guid targetUserId, CancellationToken cancellationToken)
    {
        var users = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.IsActive &&
            (x.Id == initiatorUserId || x.Id == targetUserId) &&
            x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId != null)
            .Select(x => new { x.Id, InstallationId = x.AgentInstallationId!.Value })
            .ToListAsync(cancellationToken);
        if (users.Count != 2 || users.Single(x => x.Id == initiatorUserId).InstallationId != initiatorInstallationId)
            throw new InvalidOperationException("Both coordination participants must be active same-organization agents.");
        foreach (var installationId in users.Select(x => x.InstallationId))
        {
            var grantJson = await db.AgentInstallationGrants.AsNoTracking()
                .Where(x => x.AgentInstallationId == installationId)
                .Select(x => x.RequiredCapabilitiesJson).SingleOrDefaultAsync(cancellationToken);
            var grants = JsonSerializer.Deserialize<string[]>(grantJson ?? "[]", JsonOptions) ?? [];
            if (!grants.Contains(CommunicationCapabilities.CoordinationRespond, StringComparer.Ordinal) ||
                !grants.Contains(CommunicationCapabilities.CoordinationRead, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    "Both agents must be granted coordination response and read authority.");
        }
        return users.Single(x => x.Id == targetUserId).InstallationId;
    }

    private async Task<AgentCoordinationSession> MapAsync(
        DomainSession session, CancellationToken cancellationToken)
    {
        var users = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
            x.Id == session.InitiatorOrganizationUserId || x.Id == session.TargetOrganizationUserId)
            .Include(x => x.Role).ToDictionaryAsync(x => x.Id, cancellationToken);
        AgentCoordinationParticipant Participant(Guid userId, Guid installationId)
        {
            var user = users[userId];
            return new(userId, installationId, user.DisplayName, user.Role?.Name ?? "Agent");
        }
        return new AgentCoordinationSession(
            session.Id, session.ConversationId, session.SourceConversationId,
            session.SourceChatTurnId, session.SourceMessageId,
            Participant(session.InitiatorOrganizationUserId, session.InitiatorInstallationId),
            Participant(session.TargetOrganizationUserId, session.TargetInstallationId),
            session.Subject, session.Objective,
            JsonSerializer.Deserialize<string[]>(session.SuccessCriteriaJson, JsonOptions) ?? [],
            session.Status.ToString(), session.Revision, session.NextTurnOrdinal,
            session.CurrentOrganizationUserId, session.IsFinalization, session.FinalSummary,
            session.CreatedAt, session.UpdatedAt,
            session.Turns.OrderBy(x => x.Ordinal).Select(x => new AgentCoordinationTurn(
                x.Id, x.Ordinal, x.SpeakerOrganizationUserId, x.Disposition, x.Content, x.CreatedAt,
                MapArtifact(x))).ToList());
    }

    private IQueryable<DomainSession> QuerySession() =>
        db.AgentCoordinationSessions.Include(x => x.Turns);

    private static Guid OtherParticipant(DomainSession session, Guid actorId) =>
        actorId == session.InitiatorOrganizationUserId
            ? session.TargetOrganizationUserId : session.InitiatorOrganizationUserId;

    private static Guid InstallationFor(DomainSession session, Guid userId) =>
        userId == session.InitiatorOrganizationUserId
            ? session.InitiatorInstallationId : session.TargetInstallationId;

    private static void ValidateStart(StartAgentCoordinationRequest request)
    {
        if (request.TargetOrganizationUserId == Guid.Empty || request.SourceConversationId == Guid.Empty ||
            request.SourceChatTurnId == Guid.Empty || request.SourceMessageId == Guid.Empty)
            throw new ArgumentException("Coordination target and source identities are required.");
        if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Objective) ||
            string.IsNullOrWhiteSpace(request.InitialMessage) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Coordination subject, objective, initial message, and idempotency key are required.");
        if (request.SuccessCriteria.Count == 0 || request.SuccessCriteria.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one success criterion is required.");
        ValidateArtifact(request.Artifact);
    }

    private static void ValidateResponse(RespondToAgentCoordinationRequest request)
    {
        if (request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Content) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Coordination session, content, and idempotency key are required.");
        if (request.Disposition is not (AgentCoordinationDispositions.Continue or
            AgentCoordinationDispositions.Completed or AgentCoordinationDispositions.Blocked))
            throw new ArgumentException("Disposition must be Continue, Completed, or Blocked.");
        ValidateArtifact(request.Artifact);
    }

    private static void ApplyArtifact(DomainTurn turn, AgentCoordinationArtifactSubmission? artifact)
    {
        if (artifact is null) return;
        ValidateArtifact(artifact);
        var payload = artifact.Payload.GetRawText();
        turn.ArtifactType = artifact.Type.Trim();
        turn.ArtifactSchemaVersion = artifact.SchemaVersion.Trim();
        turn.ArtifactKey = artifact.Key.Trim();
        turn.ArtifactPageOrdinal = artifact.PageOrdinal;
        turn.ArtifactIsFinalPage = artifact.IsFinalPage;
        turn.ArtifactPayloadJson = payload;
        turn.ArtifactDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static AgentCoordinationArtifact? MapArtifact(DomainTurn turn) =>
        turn.ArtifactType is null || turn.ArtifactSchemaVersion is null || turn.ArtifactKey is null ||
        turn.ArtifactPageOrdinal is null || turn.ArtifactIsFinalPage is null ||
        turn.ArtifactPayloadJson is null || turn.ArtifactDigest is null
            ? null
            : new AgentCoordinationArtifact(
                turn.ArtifactType, turn.ArtifactSchemaVersion, turn.ArtifactKey,
                turn.ArtifactPageOrdinal.Value, turn.ArtifactIsFinalPage.Value,
                JsonDocument.Parse(turn.ArtifactPayloadJson).RootElement.Clone(), turn.ArtifactDigest);

    private static void ValidateArtifact(AgentCoordinationArtifactSubmission? artifact)
    {
        if (artifact is null) return;
        if (string.IsNullOrWhiteSpace(artifact.Type) || artifact.Type.Length > 200 ||
            string.IsNullOrWhiteSpace(artifact.SchemaVersion) || artifact.SchemaVersion.Length > 50 ||
            string.IsNullOrWhiteSpace(artifact.Key) || artifact.Key.Length > 500 ||
            artifact.PageOrdinal < 0 || artifact.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ArgumentException("The coordination artifact metadata is invalid.");
        if (Encoding.UTF8.GetByteCount(artifact.Payload.GetRawText()) > 256 * 1024)
            throw new ArgumentException("A coordination artifact cannot exceed 256 KiB.");
    }
}
