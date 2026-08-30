using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using AgentWorkContext = CSweet.WorkManagement.Contracts.AgentWorkContext;
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

        var source = await db.ChatTurns.SingleOrDefaultAsync(x =>
            x.Id == request.SourceChatTurnId && x.OrganizationId == organizationId &&
            x.ConversationId == request.SourceConversationId &&
            x.UserMessageId == request.SourceMessageId,
            cancellationToken) ?? throw new InvalidOperationException(
            "The source chat turn does not belong to this organization and conversation.");
        var sourceMessage = await db.CoreConversationMessages.SingleOrDefaultAsync(x =>
            x.Id == request.SourceMessageId &&
            x.ConversationId == request.SourceConversationId,
            cancellationToken) ?? throw new InvalidOperationException(
            "The source coordination message is unavailable.");
        var isInboundSource = source.TargetAgentOrganizationUserId == initiatorOrganizationUserId;
        var isOutboundTargetedSource =
            source.TargetAgentOrganizationUserId == request.TargetOrganizationUserId &&
            sourceMessage.SenderOrganizationUserId == initiatorOrganizationUserId;
        if (!isInboundSource && !isOutboundTargetedSource)
            throw new InvalidOperationException(
                "The source chat turn does not belong to the initiating agent or target the requested collaborator.");

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
                [request.TargetOrganizationUserId],
                AudienceWorkstreamIds: request.WorkContext?.WorkstreamId is { } contextWorkstreamId ? [contextWorkstreamId] : null)
            {
                WorkstreamId = request.WorkContext?.WorkstreamId,
                TeamId = request.WorkContext?.TeamId
            },
            cancellationToken);
        var chat = chatAction.Chat ?? throw new InvalidOperationException(chatAction.Message);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var session = new DomainSession
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkstreamId = request.WorkContext?.WorkstreamId,
            TeamId = request.WorkContext?.TeamId,
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
        // Link an outbound kickoff before its ordinary message work is handled. The target agent
        // can then recognize that the message is the source of governed coordination instead of
        // producing a second, free-form acknowledgement alongside the structured turn.
        sourceMessage.CoordinationSessionId = session.Id;
        if (source.Status == ChatTurnStatus.Queued)
        {
            // Governed coordination is now the only response path for this request. Completing the
            // ordinary chat turn prevents a second dispatch through the free-form chat handler.
            source.Status = ChatTurnStatus.Completed;
            source.ResponseReadyAt = source.CompletedAt = source.UpdatedAt = now;
            source.LeaseOwner = null;
            source.LeaseUntil = null;
        }
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

    public async Task<AgentCoordinationSession> StartWorkAsync(
        Guid organizationId,
        Guid initiatorOrganizationUserId,
        Guid initiatorInstallationId,
        StartWorkItemCoordinationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkStart(request);
        var existing = await QuerySession().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.SourceKind != "WorkItem" ||
                existing.InitiatorOrganizationUserId != initiatorOrganizationUserId ||
                existing.InitiatorInstallationId != initiatorInstallationId ||
                existing.TargetOrganizationUserId != request.TargetOrganizationUserId ||
                existing.SourceBoardId != request.BoardId ||
                existing.SourceWorkItemId != request.ItemId ||
                existing.SourceSprintExecutionId != request.SprintExecutionId ||
                existing.SourceStageExecutionId != request.StageExecutionId ||
                existing.SourceAssignmentRevision != request.AssignmentRevision)
                throw new InvalidOperationException(
                    "The work coordination idempotency key is already bound to a different assignment snapshot.");
            return await MapAsync(existing, cancellationToken);
        }

        var stage = await db.WorkStageExecutions.AsNoTracking()
            .Include(x => x.ItemExecution)!.ThenInclude(x => x!.WorkItem)
            .Include(x => x.ItemExecution)!.ThenInclude(x => x!.SprintExecution)
            .SingleOrDefaultAsync(x =>
                x.Id == request.StageExecutionId &&
                x.ItemExecution!.SprintExecutionId == request.SprintExecutionId &&
                x.ItemExecution.WorkItemId == request.ItemId &&
                x.ItemExecution.SprintExecution!.OrganizationId == organizationId &&
                x.ItemExecution.SprintExecution.BoardId == request.BoardId,
                cancellationToken)
            ?? throw new InvalidOperationException("The work coordination source is stale or invalid.");
        var workItem = stage.ItemExecution!.WorkItem!;
        if (workItem.AssignmentRevision != request.AssignmentRevision)
            throw new InvalidOperationException("The work assignment changed before support could start.");
        if (stage.Status is not (WorkStageExecutionStatus.Running or WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Failed))
            throw new InvalidOperationException("Technical support requires the exact executing, blocked, or failed stage.");

        var board = await db.WorkBoards.AsNoTracking().SingleAsync(x =>
            x.Id == request.BoardId && x.OrganizationId == organizationId, cancellationToken);
        if (!board.TeamId.HasValue)
            throw new InvalidOperationException("Work-sourced coordination requires a team board.");
        var participants = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive &&
                (x.Id == initiatorOrganizationUserId || x.Id == request.TargetOrganizationUserId))
            .Include(x => x.Role)
            .Select(x => new
            {
                x.Id, x.AgentInstallationId,
                Role = x.Role == null ? string.Empty : x.Role.Name,
                InTeam = db.TeamMemberships.Any(m => m.OrganizationId == organizationId &&
                    m.TeamId == board.TeamId && m.OrganizationUserId == x.Id && m.EndedAt == null)
            }).ToListAsync(cancellationToken);
        if (participants.Count != 2 || participants.Any(x => !x.InTeam || !x.AgentInstallationId.HasValue))
            throw new UnauthorizedAccessException("Both support participants must be active agents on the work item's team.");
        var initiator = participants.Single(x => x.Id == initiatorOrganizationUserId);
        var target = participants.Single(x => x.Id == request.TargetOrganizationUserId);
        if (initiator.AgentInstallationId != initiatorInstallationId)
            throw new UnauthorizedAccessException("The initiating installation does not match the employee identity.");
        var initiatorIsDeveloper = RoleContains(initiator.Role, "Developer") &&
            stage.AgentInstallationId == initiatorInstallationId;
        var targetIsDeveloper = RoleContains(target.Role, "Developer") &&
            stage.AgentInstallationId == target.AgentInstallationId;
        var initiatorIsArchitect = RoleContains(initiator.Role, "Architect");
        var targetIsArchitect = RoleContains(target.Role, "Architect");
        if (!((initiatorIsDeveloper && targetIsArchitect) ||
              (initiatorIsArchitect && targetIsDeveloper)))
            throw new UnauthorizedAccessException(
                "Work support is limited to the exact assigned Developer and a designated team Architect.");

        var targetInstallationId = await ResolveParticipantsAsync(
            organizationId, initiatorOrganizationUserId, initiatorInstallationId,
            request.TargetOrganizationUserId, cancellationToken);
        var chatAction = await hub.CreateAsync(
            organizationId, initiatorOrganizationUserId,
            new CreateCommunicationChatRequest(
                null, $"Work support: {request.Subject.Trim()}", true, true,
                [request.TargetOrganizationUserId]), cancellationToken);
        var chat = chatAction.Chat ?? throw new InvalidOperationException(chatAction.Message);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var session = new DomainSession
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ConversationId = chat.Id,
            SourceKind = "WorkItem", SourceBoardId = request.BoardId,
            SourceWorkItemId = request.ItemId, SourceSprintExecutionId = request.SprintExecutionId,
            SourceStageExecutionId = request.StageExecutionId,
            SourceAssignmentRevision = request.AssignmentRevision, MaximumTurns = 6,
            InitiatorOrganizationUserId = initiatorOrganizationUserId,
            InitiatorInstallationId = initiatorInstallationId,
            TargetOrganizationUserId = request.TargetOrganizationUserId,
            TargetInstallationId = targetInstallationId,
            CurrentOrganizationUserId = request.TargetOrganizationUserId,
            Subject = request.Subject.Trim(), Objective = request.Objective.Trim(),
            SuccessCriteriaJson = JsonSerializer.Serialize(
                request.SuccessCriteria.Select(x => x.Trim()).ToArray(), JsonOptions),
            Status = DomainStatus.Active, Revision = 1, NextTurnOrdinal = 1,
            IdempotencyKey = request.IdempotencyKey.Trim(), CreatedAt = now, UpdatedAt = now
        };
        var initialMessage = AppendMessage(session, initiatorOrganizationUserId,
            request.InitialMessage.Trim(), $"coordination:{session.Id:N}:initial");
        var initialTurn = new DomainTurn
        {
            Id = Guid.NewGuid(), SessionId = session.Id, EventId = Guid.NewGuid(),
            SpeakerOrganizationUserId = initiatorOrganizationUserId,
            ConversationMessageId = initialMessage.Id, Ordinal = 0,
            Disposition = AgentCoordinationDispositions.Continue,
            Content = request.InitialMessage.Trim(),
            IdempotencyKey = $"coordination:{session.Id:N}:initial", CreatedAt = now
        };
        ApplyArtifact(initialTurn, request.Artifact);
        session.Turns.Add(initialTurn);
        db.AgentCoordinationSessions.Add(session);
        AppendWorkComment(session, "ArchitectureSupportRequested", request.InitialMessage.Trim(), initialTurn.ArtifactDigest);
        await db.SaveChangesAsync(cancellationToken);
        session.CurrentAgentWorkItemId = await EnqueueTurnAsync(
            session, targetInstallationId, request.TargetOrganizationUserId, cancellationToken);
        initialTurn.AgentWorkItemId = session.CurrentAgentWorkItemId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    public async Task<AgentCoordinationSession> StartBoardAsync(
        Guid organizationId,
        Guid initiatorOrganizationUserId,
        Guid initiatorInstallationId,
        StartBoardCoordinationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateBoardStart(request);
        var existing = await QuerySession().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.SourceKind != "Board" || existing.SourceBoardId != request.BoardId ||
                existing.InitiatorOrganizationUserId != initiatorOrganizationUserId ||
                existing.TargetOrganizationUserId != request.TargetOrganizationUserId)
                throw new InvalidOperationException(
                    "The board coordination idempotency key is already bound to another collaboration.");
            return await MapAsync(existing, cancellationToken);
        }

        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.BoardId && x.OrganizationId == organizationId &&
            x.ArchivedAt == null && x.TeamId != null, cancellationToken)
            ?? throw new InvalidOperationException("Board coordination requires an active team board.");
        var participantIds = new[] { initiatorOrganizationUserId, request.TargetOrganizationUserId };
        var participants = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                participantIds.Contains(x.Id) && x.OrganizationId == organizationId &&
                x.IsActive && x.AgentInstallationId != null)
            .Select(x => new
            {
                x.Id,
                InstallationId = x.AgentInstallationId!.Value,
                InTeam = db.TeamMemberships.Any(m => m.OrganizationId == organizationId &&
                    m.TeamId == board.TeamId && m.OrganizationUserId == x.Id && m.EndedAt == null)
            }).ToListAsync(cancellationToken);
        if (participants.Count != 2 || participants.Any(x => !x.InTeam) ||
            participants.Single(x => x.Id == initiatorOrganizationUserId).InstallationId != initiatorInstallationId)
            throw new UnauthorizedAccessException(
                "Both board-collaboration participants must be active agents on the board team.");
        if (board.ManagerOrganizationUserId != initiatorOrganizationUserId &&
            board.ManagerOrganizationUserId != request.TargetOrganizationUserId)
            throw new UnauthorizedAccessException(
                "Board coordination must include the accountable board manager.");
        var targetInstallationId = participants.Single(x =>
            x.Id == request.TargetOrganizationUserId).InstallationId;
        var chatAction = await hub.CreateAsync(
            organizationId, initiatorOrganizationUserId,
            new CreateCommunicationChatRequest(
                null, $"Board planning: {request.Subject.Trim()}", true, true,
                [request.TargetOrganizationUserId]), cancellationToken);
        var chat = chatAction.Chat ?? throw new InvalidOperationException(chatAction.Message);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var session = new DomainSession
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ConversationId = chat.Id,
            SourceKind = "Board", SourceBoardId = request.BoardId, MaximumTurns = 18,
            InitiatorOrganizationUserId = initiatorOrganizationUserId,
            InitiatorInstallationId = initiatorInstallationId,
            TargetOrganizationUserId = request.TargetOrganizationUserId,
            TargetInstallationId = targetInstallationId,
            CurrentOrganizationUserId = request.TargetOrganizationUserId,
            Subject = request.Subject.Trim(), Objective = request.Objective.Trim(),
            SuccessCriteriaJson = JsonSerializer.Serialize(
                request.SuccessCriteria.Select(x => x.Trim()).ToArray(), JsonOptions),
            Status = DomainStatus.Active, Revision = 1, NextTurnOrdinal = 1,
            IdempotencyKey = request.IdempotencyKey.Trim(), CreatedAt = now, UpdatedAt = now
        };
        var initialMessage = AppendMessage(session, initiatorOrganizationUserId,
            request.InitialMessage.Trim(), $"coordination:{session.Id:N}:initial");
        var initialTurn = new DomainTurn
        {
            Id = Guid.NewGuid(), SessionId = session.Id, EventId = Guid.NewGuid(),
            SpeakerOrganizationUserId = initiatorOrganizationUserId,
            ConversationMessageId = initialMessage.Id, Ordinal = 0,
            Disposition = AgentCoordinationDispositions.Continue,
            Content = request.InitialMessage.Trim(),
            IdempotencyKey = $"coordination:{session.Id:N}:initial", CreatedAt = now
        };
        ApplyArtifact(initialTurn, request.Artifact);
        session.Turns.Add(initialTurn);
        db.AgentCoordinationSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        session.CurrentAgentWorkItemId = await EnqueueTurnAsync(
            session, targetInstallationId, request.TargetOrganizationUserId, cancellationToken);
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
        if (request.Disposition == AgentCoordinationDispositions.Continue &&
            session.MaximumTurns.HasValue && request.ExpectedTurnOrdinal >= session.MaximumTurns.Value)
            throw new InvalidOperationException("The technical-support coordination turn limit was reached.");
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
            AppendWorkCompletionComment(session, request.Content.Trim());
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
            AppendWorkCompletionComment(session, request.Content.Trim());
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
            // A resumed logical turn has the same ordinal but a new session revision and
            // therefore a different payload. Include the revision so recovery never collides
            // with the failed delivery's idempotency record.
            $"coordination:{session.Id:N}:turn:{session.NextTurnOrdinal}:revision:{session.Revision}",
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
            self, other, session.IsFinalization, mapped.Turns)
        {
            SourceKind = mapped.SourceKind,
            WorkSource = mapped.WorkSource,
            BoardSource = mapped.BoardSource,
            MaximumTurns = mapped.MaximumTurns,
            WorkContext = mapped.WorkContext
        };
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
        if (!session.SourceConversationId.HasValue || session.SourceConversationId == session.ConversationId)
            return;

        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = session.SourceConversationId.Value,
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
            session.Id, session.ConversationId, session.SourceConversationId ?? Guid.Empty,
            session.SourceChatTurnId ?? Guid.Empty, session.SourceMessageId ?? Guid.Empty,
            Participant(session.InitiatorOrganizationUserId, session.InitiatorInstallationId),
            Participant(session.TargetOrganizationUserId, session.TargetInstallationId),
            session.Subject, session.Objective,
            JsonSerializer.Deserialize<string[]>(session.SuccessCriteriaJson, JsonOptions) ?? [],
            session.Status.ToString(), session.Revision, session.NextTurnOrdinal,
            session.CurrentOrganizationUserId, session.IsFinalization, session.FinalSummary,
            session.CreatedAt, session.UpdatedAt,
            session.Turns.OrderBy(x => x.Ordinal).Select(x => new AgentCoordinationTurn(
                x.Id, x.Ordinal, x.SpeakerOrganizationUserId, x.Disposition, x.Content, x.CreatedAt,
                MapArtifact(x))).ToList())
        {
            SourceKind = session.SourceKind,
            WorkSource = session.SourceKind == "WorkItem" &&
                session.SourceBoardId.HasValue && session.SourceWorkItemId.HasValue &&
                session.SourceSprintExecutionId.HasValue && session.SourceStageExecutionId.HasValue &&
                session.SourceAssignmentRevision.HasValue
                ? new AgentCoordinationWorkSource(
                    session.SourceBoardId.Value, session.SourceWorkItemId.Value,
                    session.SourceSprintExecutionId.Value, session.SourceStageExecutionId.Value,
                    session.SourceAssignmentRevision.Value)
                : null,
            BoardSource = session.SourceKind == "Board" && session.SourceBoardId.HasValue
                ? new AgentCoordinationBoardSource(session.SourceBoardId.Value)
                : null,
            MaximumTurns = session.MaximumTurns,
            WorkContext = session.WorkstreamId.HasValue
                ? new AgentWorkContext(session.OrganizationId, session.WorkstreamId.Value, session.TeamId,
                    session.SourceBoardId, session.SourceWorkItemId, null, null, session.Id, null, null)
                : null
        };
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

    private static void ValidateWorkStart(StartWorkItemCoordinationRequest request)
    {
        if (request.TargetOrganizationUserId == Guid.Empty || request.BoardId == Guid.Empty ||
            request.ItemId == Guid.Empty || request.SprintExecutionId == Guid.Empty ||
            request.StageExecutionId == Guid.Empty || request.AssignmentRevision <= 0)
            throw new ArgumentException("Work coordination target and source identities are required.");
        if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Objective) ||
            string.IsNullOrWhiteSpace(request.InitialMessage) || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.SuccessCriteria.Count == 0 || request.SuccessCriteria.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Work coordination content and success criteria are required.");
        ValidateArtifact(request.Artifact);
    }

    private static void ValidateBoardStart(StartBoardCoordinationRequest request)
    {
        if (request.TargetOrganizationUserId == Guid.Empty || request.BoardId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Objective) ||
            string.IsNullOrWhiteSpace(request.InitialMessage) || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.SuccessCriteria.Count == 0 || request.SuccessCriteria.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Board coordination target, board, content, and success criteria are required.");
        ValidateArtifact(request.Artifact);
    }

    private void AppendWorkCompletionComment(DomainSession session, string summary)
    {
        if (session.SourceKind != "WorkItem" || !session.SourceWorkItemId.HasValue)
            return;
        var digest = session.Turns.OrderByDescending(x => x.Ordinal)
            .Select(x => x.ArtifactDigest).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        AppendWorkComment(session, "ArchitectureSupportCompleted", summary, digest);
    }

    private void AppendWorkComment(DomainSession session, string kind, string body, string? artifactDigest)
    {
        if (!session.SourceWorkItemId.HasValue)
            return;
        db.WorkItemComments.Add(new WorkItemComment
        {
            Id = Guid.NewGuid(), OrganizationId = session.OrganizationId,
            WorkItemId = session.SourceWorkItemId.Value,
            AuthorKind = GrantSubjectKind.AutomationIdentity,
            AuthorSubjectId = session.Id, AuthorDisplayName = "C-Sweet coordination",
            Body = body.Length <= 8192 ? body : body[..8192], Kind = kind,
            CoordinationSessionId = session.Id, CausationId = session.Id.ToString("D"),
            ArtifactDigest = artifactDigest,
            IdempotencyKey = $"coordination:{session.Id:N}:{kind}", CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static bool RoleContains(string role, string value) =>
        role.Contains(value, StringComparison.OrdinalIgnoreCase);

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
