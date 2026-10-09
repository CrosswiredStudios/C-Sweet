using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkInstructionPublicationService(CSweetDbContext db, IScopedActionAuthorizationService authorization, IMemoryStore memory)
    : IWorkInstructionPublicationService
{
    public const string CommentKind = "human.instruction";
    private sealed record Target(OrganizationUser Actor, WorkTask Item, WorkBoard Board, ScopedAuthorizationDecision Grant);
    private sealed record Input(Target Target, ConversationMessage Message, Guid Employee, string Instruction, string ReviewToken);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public async Task<WorkInstructionPreview> PreviewAsync(Guid organization, Guid board, Guid item, Guid user,
        SelectWorkInstructionRequest request, CancellationToken token = default)
    {
        RequireBackend();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var input = await ReadInputAsync(organization, board, item, user, request, token);
        await transaction.CommitAsync(token);
        return new(input.Instruction, input.Target.Item.Title, input.Target.Board.Name, input.ReviewToken);
    }

    public async Task<WorkInstructionPublicationResponse> PublishAsync(Guid organization, Guid board, Guid item, Guid user,
        PublishWorkInstructionRequest request, CancellationToken token = default)
    {
        RequireBackend();
        await memory.InitializeAsync(token);
        if (request.OperationId == Guid.Empty || request.Selection is null || request.ReviewToken is not { Length: 64 })
            throw new ArgumentException("A reviewed instruction and operation identity are required.");
        var requestHash = Hash(JsonSerializer.Serialize(new { organization, board, item, user, request.Selection, request.ReviewToken }));
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            // Serialize one operation across callers, without an unbounded lock wait.
            var key = $"work-instruction:{organization:D}:{request.OperationId:D}";
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key},0)) AS \"Value\"").SingleAsync(token))
                throw new DbUpdateConcurrencyException("This publication is being processed. Retry the same operation.");
            var prior = await db.WorkInstructionPublications.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organization && x.OperationId == request.OperationId, token);
            if (prior is not null)
            {
                if (prior.ActorApplicationUserId != user || prior.RequestHash != requestHash)
                    throw new DbUpdateConcurrencyException("This operation identity was used for a different publication.");
                await RequireTargetAsync(organization, board, item, user, writing: false, token);
                var replay = await ResponseAsync(prior, replayed: true, token);
                await transaction.CommitAsync(token);
                return replay;
            }
            var input = await ReadInputAsync(organization, board, item, user, request.Selection, token);
            if (input.ReviewToken != request.ReviewToken)
                throw new DbUpdateConcurrencyException("The instruction or its audience changed. Review the current selection.");
            var now = DateTimeOffset.UtcNow;
            var publication = new WorkInstructionPublication
            {
                Id = Guid.NewGuid(), OrganizationId = organization, OperationId = request.OperationId,
                ActorApplicationUserId = user, ActorOrganizationUserId = input.Target.Actor.Id,
                SourceEmployeeId = input.Employee, SourceConversationId = request.Selection.ConversationId,
                SourceMessageId = input.Message.Id, SourceChecksum = Hash(input.Message.Content),
                // Receipt positions use Unicode scalar values, matching PostgreSQL
                // substring; the browser request uses UTF-16 selection positions.
                SelectionOffset = input.Message.Content[..request.Selection.Offset].EnumerateRunes().Count(),
                SelectionLength = input.Instruction.EnumerateRunes().Count(),
                WorkItemId = item, PublishedBoardId = board, CommentId = Guid.NewGuid(),
                InstructionChecksum = Hash(input.Instruction), RequestHash = requestHash, CreatedAt = now
            };
            var comment = new WorkItemComment
            {
                Id = publication.CommentId, OrganizationId = organization, WorkItemId = item,
                AuthorKind = GrantSubjectKind.OrganizationUser, AuthorSubjectId = input.Target.Actor.Id,
                AuthorDisplayName = input.Target.Actor.DisplayName, Body = input.Instruction, Kind = CommentKind,
                CausationId = publication.Id.ToString("D"), ArtifactDigest = publication.InstructionChecksum,
                IdempotencyKey = "instruction:" + request.OperationId.ToString("N"), CreatedAt = now
            };
            db.WorkItemComments.Add(comment);
            db.WorkInstructionPublications.Add(publication);
            // Source conversation IDs/checksums and private surrounding text never enter
            // the shared activity, audit, or wake payload. The private receipt owns them.
            var metadata = JsonSerializer.Serialize(new { publicationId = publication.Id, commentId = comment.Id, boardId = board, itemId = item });
            db.WorkItemActivities.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = organization, BoardId = board, WorkItemId = item,
                EventType = "instruction.published", Action = WorkItemActions.Comment,
                ActorKind = GrantSubjectKind.OrganizationUser, ActorSubjectId = input.Target.Actor.Id,
                ActorDisplayName = input.Target.Actor.DisplayName, AuthorizingGrantId = input.Target.Grant.GrantId,
                AuthorizingGrantRevision = input.Target.Grant.GrantRevision, DataJson = metadata, OccurredAt = now
            });
            db.QueueAudit(new AuditEventWriteRequest("work.instruction.published.v1", "WorkManagement", OrganizationId: organization,
                EntityType: nameof(WorkInstructionPublication), EntityId: publication.Id,
                Summary: "A human published a selected instruction to a canonical work item.", MetadataJson: metadata,
                Actor: new AuditActor("Human", ApplicationUserId: user, OrganizationUserId: input.Target.Actor.Id),
                EventId: publication.Id, CorrelationId: request.OperationId.ToString("D"), UseAmbientOrganization: false));
            await WorkItemDiscussion.QueueAsync(db, board, comment, "comment.created", token);
            await QueueRealtimeAsync(input.Target, comment, token);
            await db.SaveChangesAsync(token);
            await WorkInstructionMemorySource.CaptureAsync(db, comment.Id, token);
            await transaction.CommitAsync(token);
            return new(publication.Id, item, comment.Id, "Published", false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<WorkInstructionPublicationPage> ListAsync(Guid organization, Guid board, Guid item, Guid user,
        Guid? cursor = null, CancellationToken token = default)
    {
        RequireBackend();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await RequireTargetAsync(organization, board, item, user, writing: false, token);
        var query = db.WorkInstructionPublications.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.ActorApplicationUserId == user && x.WorkItemId == item);
        if (cursor is { } id)
        {
            var before = await query.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new ArgumentException("Invalid publication cursor.");
            query = query.Where(x => x.CreatedAt < before.CreatedAt || x.CreatedAt == before.CreatedAt && x.Id.CompareTo(before.Id) < 0);
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(21).ToListAsync(token);
        var items = new List<WorkInstructionPublicationResponse>();
        foreach (var row in rows.Take(20)) items.Add(await ResponseAsync(row, replayed: true, token));
        await transaction.CommitAsync(token);
        return new(items, rows.Count > 20 ? rows[19].Id : null);
    }

    private async Task<Input> ReadInputAsync(Guid organization, Guid board, Guid item, Guid user,
        SelectWorkInstructionRequest request, CancellationToken token)
    {
        if (request is null || request.ConversationId == Guid.Empty || request.MessageId == Guid.Empty || request.Offset < 0 || request.Length is < 1 or > 8192)
            throw new ArgumentException("Select at most 8192 characters from one message.");
        var target = await RequireTargetAsync(organization, board, item, user, writing: true, token);
        await LockAsync($"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={request.ConversationId} FOR SHARE NOWAIT", token);
        await LockAsync($"SELECT 1 FROM \"CoreConversationMessages\" WHERE \"Id\"={request.MessageId} FOR SHARE NOWAIT", token);
        var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ConversationId &&
            x.OrganizationId == organization && x.Kind == ConversationKind.DirectHumanAgent && x.InitiatedByOrganizationUserId == target.Actor.Id &&
            x.ArchivedAt == null && x.MergedIntoConversationId == null, token) ?? throw new UnauthorizedAccessException();
        var message = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.MessageId &&
            x.ConversationId == conversation.Id && x.Role == ConversationRole.User && x.SenderOrganizationUserId == target.Actor.Id &&
            x.Content.Length <= 65536, token)
            ?? throw new UnauthorizedAccessException();
        if (request.Offset > message.Content.Length || request.Length > message.Content.Length - request.Offset ||
            request.Offset > 0 && char.IsLowSurrogate(message.Content[request.Offset]) ||
            request.Offset + request.Length < message.Content.Length && char.IsLowSurrogate(message.Content[request.Offset + request.Length]))
            throw new ArgumentException("Invalid instruction selection.");
        var instruction = message.Content.Substring(request.Offset, request.Length);
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("The instruction cannot be empty.");
        // Prevent a pending publication from reviving a forgotten or invalidated source.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"MemoryCaptureExclusions\",\"MemorySourceInvalidations\" IN SHARE MODE", token);
        if (await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == message.Id, token) ||
            await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == message.Id, token)) throw new UnauthorizedAccessException();
        var employee = conversation.AgentOrganizationUserId ?? throw new UnauthorizedAccessException();
        var partitions = new[] { EmployeeMemoryNamespaces.Case(organization.ToString("D"), item.ToString("D"), "csweet").Partition,
            new MemoryPartition(organization.ToString("D"), "csweet", ConversationId: conversation.Id.ToString("D")) };
        foreach (var partition in partitions)
            await MemoryScopedAudienceAuthorization.RequireAsync(db, organization, employee, target.Actor.Id, partition, token, lockAuthority: true);
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employee).Select(x => x.AgentInstallationId).SingleAsync(token)
            ?? throw new UnauthorizedAccessException();
        await LockAsync($"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\"={installation} FOR SHARE NOWAIT", token);
        var authority = await MemoryScopedAudienceAuthorization.AuthorityHashAsync(db, organization, employee, target.Actor.Id, partitions, token);
        var people = await MemoryAccessAuthorityEvidence.ReadAsync(db, [("person", target.Actor.Id), ("person", employee),
            ("conversation", conversation.Id), ("installation", installation)], token);
        var grantVersion = await db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM \"ScopedActionGrants\" WHERE \"Id\"={target.Grant.GrantId!.Value}").SingleAsync(token);
        var review = Hash(JsonSerializer.Serialize(new { organization, board, item, user, request, source = Hash(message.Content),
            authority, people, target.Item.PlanningRevision, grant = target.Grant, grantVersion }));
        return new(target, message, employee, instruction, review);
    }

    private async Task<Target> RequireTargetAsync(Guid organization, Guid boardId, Guid itemId, Guid user, bool writing, CancellationToken token)
    {
        await LockAsync($"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"OrganizationId\"={organization} AND \"ApplicationUserId\"={user} FOR SHARE NOWAIT", token);
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.ApplicationUserId == user && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new UnauthorizedAccessException();
        await LockAsync($"SELECT 1 FROM \"CoreWorkTasks\" WHERE \"Id\"={itemId} FOR SHARE NOWAIT", token);
        await LockAsync($"SELECT 1 FROM \"WorkBoards\" WHERE \"Id\"={boardId} FOR SHARE NOWAIT", token);
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId && x.OrganizationId == organization &&
            x.BoardId == boardId && (!writing || x.ArchivedAt == null), token) ?? throw new UnauthorizedAccessException();
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == boardId && x.OrganizationId == organization &&
            (!writing || x.ArchivedAt == null), token) ?? throw new UnauthorizedAccessException();
        if (board.Kind == WorkBoardKind.Personal && board.OwnerOrganizationUserId != actor.Id) throw new UnauthorizedAccessException();
        await LockAsync($"SELECT 1 FROM \"ScopedActionGrants\" WHERE \"OrganizationId\"={organization} AND \"SubjectId\"={actor.Id} ORDER BY \"Id\" FOR SHARE NOWAIT", token);
        ScopedAuthorizationDecision? permission = null;
        foreach (var action in new[] { board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkItemActions.Read,
            WorkItemActions.ReadComments }.Concat(writing ? [WorkItemActions.Comment] : Array.Empty<string>()))
        {
            var decision = await authorization.AuthorizeAsync(organization, GrantSubjectKind.OrganizationUser, actor.Id, action, GrantScopeKind.WorkItem, itemId, token);
            if (!decision.Allowed) decision = await authorization.AuthorizeAsync(organization, GrantSubjectKind.OrganizationUser, actor.Id, action, GrantScopeKind.Board, boardId, token);
            if (!decision.Allowed && board.TeamId is { } team) decision = await authorization.AuthorizeAsync(organization, GrantSubjectKind.OrganizationUser, actor.Id, action, GrantScopeKind.Team, team, token);
            if (!decision.Allowed) throw new UnauthorizedAccessException();
            permission = decision;
        }
        return new(actor, item, board, permission!);
    }

    private async Task<WorkInstructionPublicationResponse> ResponseAsync(WorkInstructionPublication receipt, bool replayed, CancellationToken token)
    {
        var comment = await db.WorkItemComments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.CommentId &&
            x.OrganizationId == receipt.OrganizationId && x.WorkItemId == receipt.WorkItemId, token);
        var status = comment is null || comment.DeletedAt is not null ? "Withdrawn" :
            comment.Revision == 1 && Hash(comment.Body) == receipt.InstructionChecksum ? "Published" : "Changed";
        return new(receipt.Id, receipt.WorkItemId, receipt.CommentId, status, replayed);
    }

    private async Task QueueRealtimeAsync(Target target, WorkItemComment comment, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == comment.OrganizationId &&
            x.SubjectKind == GrantSubjectKind.OrganizationUser && x.Action == WorkItemActions.ReadComments &&
            x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > now) &&
            (x.ScopeKind == GrantScopeKind.Organization || x.ScopeKind == GrantScopeKind.WorkItem && x.ScopeId == target.Item.Id ||
             x.ScopeKind == GrantScopeKind.Board && x.ScopeId == target.Board.Id ||
             x.ScopeKind == GrantScopeKind.Team && x.ScopeId == target.Board.TeamId))
            .Select(x => x.SubjectId).Distinct().ToListAsync(token);
        var humans = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == comment.OrganizationId &&
            x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null && candidates.Contains(x.Id) &&
            (target.Board.Kind != WorkBoardKind.Personal || x.Id == target.Board.OwnerOrganizationUserId)).Select(x => x.Id).ToListAsync(token);
        var recipients = new List<Guid>();
        foreach (var human in humans)
        {
            var allowed = true;
            foreach (var action in new[] { target.Board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkItemActions.Read,
                WorkItemActions.ReadComments })
            {
                var decision = await authorization.AuthorizeAsync(comment.OrganizationId, GrantSubjectKind.OrganizationUser, human,
                    action, GrantScopeKind.WorkItem, target.Item.Id, token);
                if (!decision.Allowed) decision = await authorization.AuthorizeAsync(comment.OrganizationId, GrantSubjectKind.OrganizationUser,
                    human, action, GrantScopeKind.Board, target.Board.Id, token);
                if (!decision.Allowed && target.Board.TeamId is { } team) decision = await authorization.AuthorizeAsync(comment.OrganizationId,
                    GrantSubjectKind.OrganizationUser, human, action, GrantScopeKind.Team, team, token);
                if (!decision.Allowed) { allowed = false; break; }
            }
            if (allowed) recipients.Add(human);
        }
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = comment.OrganizationId,
            RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipients), EventType = AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{comment.OrganizationId:D}/work/boards/{target.Board.Id:D}",
            DataJson = JsonSerializer.Serialize(new { boardId = target.Board.Id, itemId = target.Item.Id,
                changeType = "instruction.published", revision = comment.Revision }),
            Status = ApplicationRealtimeOutboxStatus.Pending, OccurredAt = now, NextAttemptAt = now
        });
    }

    private void RequireBackend()
    {
        if (!db.Database.IsNpgsql()) throw new NotSupportedException("Instruction publication requires PostgreSQL.");
    }
    private async Task LockAsync(FormattableString sql, CancellationToken token)
    {
        try { await db.Database.ExecuteSqlInterpolatedAsync(sql, token); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("The publication source or audience is changing. Refresh before continuing.", error); }
    }
}
