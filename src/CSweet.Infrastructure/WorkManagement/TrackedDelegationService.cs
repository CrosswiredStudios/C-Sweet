using System.Security.Cryptography;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Application.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using DomainUpdate = CSweet.Domain.WorkManagement.WorkTrackingUpdate;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class TrackedDelegationService(CSweetDbContext db, IPersonalTodoService todos, TimeProvider clock, IChatTurnService turns) : ITrackedDelegationService
{
    private static readonly ActivitySource Traces = new("CSweet.Delegation");
    private static readonly Meter Metrics = new("CSweet.Delegation");
    private static readonly Counter<long> Receipts = Metrics.CreateCounter<long>("delegation.receipts");
    private static readonly Counter<long> Attention = Metrics.CreateCounter<long>("delegation.attention");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => clock.GetUtcNow();
    private static bool Closed(string status) => status is "Completed" or "Declined" or "Cancelled";
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    private static void Text(string value, int limit = 4000)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > limit) throw new ArgumentException("Text is empty or exceeds its bound."); }
    private static void Key(string value) => Text(value, 160);
    private static Guid[] Parties(TrackedDelegation d) => JsonSerializer.Deserialize<Guid[]>(d.InterestedPartiesJson, Json) ?? [];
    private static bool Participant(TrackedDelegation d, Guid actor) => d.CoordinatorId == actor || d.RecipientId == actor ||
        d.OriginalRequesterId == actor || Parties(d).Contains(actor);

    private async Task<T> Mutate<T>(Func<Task<T>> action, CancellationToken token)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        var result = await action();
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private async Task<OrganizationUser> Actor(Guid organization, Guid actor, CancellationToken token) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == actor &&
            x.IsActive && x.ArchivedAt == null, token) ?? throw new UnauthorizedAccessException("The employee is inactive.");

    private async Task<TrackedDelegation> Load(Guid organization, Guid actor, Guid id, CancellationToken token)
    {
        await Actor(organization, actor, token);
        var d = await db.TrackedDelegations.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == id, token)
            ?? throw new KeyNotFoundException("Delegation not found.");
        if (!Participant(d, actor)) throw new UnauthorizedAccessException();
        return d;
    }

    public Task<DelegationSnapshot> CreateAsync(Guid organization, Guid actor, DelegateWithTrackingRequest request, CancellationToken token = default) =>
        Mutate(async () =>
        {
            Key(request.IdempotencyKey); Text(request.Instruction); Text(request.IntendedOutcome);
            var coordinator = await Actor(organization, actor, token);
            var recipient = await Actor(organization, request.RecipientOrganizationUserId, token);
            if (coordinator.EmployeeType != EmployeeType.Agent || recipient.EmployeeType != EmployeeType.Agent || actor == recipient.Id)
                throw new ArgumentException("Delegation requires two distinct active agents.");
            var source = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == request.SourceMessageId && x.ConversationId == request.SourceConversationId &&
                x.Conversation!.OrganizationId == organization && x.Conversation.ArchivedAt == null, token)
                ?? throw new UnauthorizedAccessException("The instruction source is unavailable.");
            if (!await db.ConversationParticipants.AnyAsync(x => x.ConversationId == request.SourceConversationId &&
                    x.OrganizationUserId == actor && x.LeftAt == null, token) || source.SenderOrganizationUserId is not { } requester)
                throw new UnauthorizedAccessException();
            await Actor(organization, requester, token);
            // Model output selects the instruction; it cannot publish unrelated private material.
            if (!source.Content.Contains(request.Instruction, StringComparison.Ordinal))
                throw new ArgumentException("The delegated instruction must be an exact selection from the source message.");
            var fingerprint = Hash(request);
            var existing = await db.TrackedDelegations.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                x.AuthoredByOrganizationUserId == actor && x.IdempotencyKey == request.IdempotencyKey, token);
            if (existing is not null)
            {
                if (existing.RequestHash != fingerprint) throw new DbUpdateConcurrencyException("The operation was reused with different input.");
                await RequireSource(existing, token);
                return await Map(existing, token);
            }
            var interested = (request.InterestedParties ?? []).Append(requester).Distinct().ToArray();
            if (interested.Length > 32) throw new ArgumentException("Too many interested parties.");
            var sourceAudience = await db.ConversationParticipants.Where(x => x.ConversationId == source.ConversationId && x.LeftAt == null)
                .Select(x => x.OrganizationUserId).ToListAsync(token);
            var mentioned = await db.ConversationMessageMentions.Where(x => x.MessageId == source.Id)
                .Select(x => x.MentionedOrganizationUserId).ToListAsync(token);
            foreach (var party in interested)
            {
                await Actor(organization, party, token);
                if (!sourceAudience.Contains(party) && !mentioned.Contains(party))
                    throw new UnauthorizedAccessException("An interested party must be in the source audience or explicitly mentioned.");
            }
            TrackedDelegation? parent = null;
            if (request.ParentDelegationId is { } parentId)
            {
                parent = await Load(organization, actor, parentId, token);
                await RequireSource(parent, token);
                if (Closed(parent.Status) || parent.RecipientId != actor)
                    throw new UnauthorizedAccessException("Only the current delegate may delegate its outstanding work further.");
                if (source.ConversationId != parent.RecipientConversationId ||
                    source.SenderOrganizationUserId != parent.CoordinatorId && source.SenderOrganizationUserId != actor)
                    throw new UnauthorizedAccessException("A child instruction must be explicitly published in its parent's execution conversation.");
                requester = parent.OriginalRequesterId;
            }
            if (request.WorkContext?.WorkItemId is { } item) await RequireItem(organization, actor, item, false, token);
            if (request.WorkContext?.WorkstreamId is { } project && !await db.Workstreams.AnyAsync(x => x.Id == project && x.OrganizationId == organization, token))
                throw new ArgumentException("Unknown project context.");
            if (request.WorkContext?.TeamId is { } team && !await db.OrganizationTeams.AnyAsync(x => x.Id == team && x.OrganizationId == organization && x.ArchivedAt == null, token))
                throw new ArgumentException("Unknown team context.");
            var id = Guid.NewGuid();
            var commitment = await todos.AddAsync(organization, new(actor, coordinator.AgentInstallationId),
                new AddPersonalTodoItemRequest("Track delegated outcome", request.IntendedOutcome, "Medium", null,
                    $"delegation-followup:{id:N}", CorrelationId: $"delegation-tracking:{id:N}") { StartInBacklog = true }, token);
            var commitmentEntity = await db.CoreWorkTasks.SingleAsync(x => x.Id == commitment.Id, token);
            commitmentEntity.IsExecutable = false;
            commitmentEntity.SourceConversationId = source.ConversationId;
            commitmentEntity.SourceMessageId = source.Id;
            var chat = await DirectChat(organization, actor, recipient.Id, token);
            var instruction = Message(chat.Id, actor, request.Instruction, $"delegation-instruction:{id:N}", id);
            var d = new TrackedDelegation
            {
                Id = id, OrganizationId = organization, CoordinatorId = actor, RecipientId = recipient.Id,
                AuthoredByOrganizationUserId = actor, InstructionAuthorId = source.SenderOrganizationUserId!.Value,
                OriginalRequesterId = requester, SourceConversationId = source.ConversationId, SourceMessageId = source.Id,
                SourceChecksum = Hash(source.Content), ReportingConversationId = source.ConversationId,
                RecipientConversationId = chat.Id, RecipientMessageId = instruction.Id, CommitmentItemId = commitment.Id,
                ParentDelegationId = parent?.Id, Instruction = request.Instruction, IntendedOutcome = request.IntendedOutcome,
                InterestedPartiesJson = JsonSerializer.Serialize(interested, Json),
                WorkContextJson = request.WorkContext is null ? null : JsonSerializer.Serialize(request.WorkContext, Json),
                IsDirective = await IsAncestor(organization, actor, recipient.Id, token) &&
                    (source.SenderOrganizationUserId == actor || await IsAncestor(organization, source.SenderOrganizationUserId!.Value, actor, token)),
                IdempotencyKey = request.IdempotencyKey, RequestHash = fingerprint, CreatedAt = Now, UpdatedAt = Now, LastSubstantiveAt = Now
            };
            db.TrackedDelegations.Add(d);
            Wake(organization, recipient, DelegationCapabilities.Received, new DelegationWake(id), $"delegation-received:{id:N}");
            await db.SaveChangesAsync(token);
            return await Map(d, token);
        }, token);

    public async Task<IReadOnlyList<DelegationSnapshot>> ReadAsync(Guid organization, Guid actor, ReadDelegationRequest request, CancellationToken token = default)
    {
        await Actor(organization, actor, token);
        if (request.ExecutionMessageId is { } message)
        {
            var execution = await db.TrackedDelegations.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                x.RecipientId == actor && x.ExecutionMessageId == message, token);
            if (execution is null) return [];
            await RequireSource(execution, token);
            return [await Map(execution, token, true)];
        }
        if (request.DelegationId is { } id)
        {
            var d = await Load(organization, actor, id, token);
            await RequireSource(d, token);
            return [await Map(d, token, actor == d.RecipientId)];
        }
        var records = await db.TrackedDelegations.Where(x => x.OrganizationId == organization &&
            (x.RecipientId == actor || x.TrackingCancelledAt == null && (x.CoordinatorId == actor || x.OriginalRequesterId == actor)) &&
            x.Status != "Completed" && x.Status != "Cancelled" && x.Status != "Declined")
            .OrderBy(x => x.UpdatedAt).Take(50).ToListAsync(token);
        var result = new List<DelegationSnapshot>();
        foreach (var d in records) result.Add(await Map(d, token, actor == d.RecipientId));
        return result;
    }

    public Task<DelegationSnapshot> RespondAsync(Guid organization, Guid actor, DelegationResponseRequest request, CancellationToken token = default) =>
        Mutate(async () =>
        {
            Key(request.IdempotencyKey); Text(request.Summary);
            var d = await Load(organization, actor, request.DelegationId, token);
            if (d.RecipientId != actor) throw new UnauthorizedAccessException();
            WorkTask? outcomeItem = null;
            await RequireSource(d, token);
            var receipt = await db.WorkTrackingUpdates.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                x.AuthorId == actor && x.IdempotencyKey == request.IdempotencyKey, token);
            if (receipt is not null)
            {
                if (receipt.RequestHash != Hash(request with { ExpectedRevision = 0 })) throw new DbUpdateConcurrencyException("The response input changed.");
                return await Map(d, token);
            }
            if (Closed(d.Status) || d.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Delegation changed.");
            if (d.IsDirective && !await IsAncestor(organization, d.CoordinatorId, actor, token))
                throw new UnauthorizedAccessException("The reporting relationship changed; renew this request under current authority.");
            if (request.Disposition is not ("Accepted" or "Clarification" or "Declined" or "Progress" or "Blocked" or "Completed" or "Failed"))
                throw new ArgumentException("Unsupported delegation response.");
            if (request.Disposition == "Accepted")
            {
                if (request.WorkItemId is { } linked)
                {
                    var work = await RequireItem(organization, actor, linked, false, token);
                    if (work.AssignedEmployeeId != actor && work.AccountableOrganizationUserId != actor)
                        throw new UnauthorizedAccessException("The linked work must be owned by the recipient.");
                    if (d.WorkItemId is { } oldId && oldId != linked)
                    {
                        var old = await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.Id == oldId && x.OrganizationId == organization, token);
                        if (old is { IsExecutable: false } && old.CorrelationId == $"delegation-action:{d.Id:N}")
                        {
                            // Retain provenance without leaving a second open management placeholder.
                            old.ArchivedAt = Now; old.UpdatedAt = Now; old.Revision++;
                            old.ResultSummary = $"Tracking follows canonical work {linked:D}.";
                        }
                    }
                    d.WorkItemId = linked;
                }
                else
                {
                    var person = await Actor(organization, actor, token);
                    var item = await todos.AddAsync(organization, new(actor, person.AgentInstallationId),
                        new AddPersonalTodoItemRequest("Address delegated request", $"Management commitment for delegation {d.Id:D}. Read the delegation for its instruction and intended outcome.",
                            "Medium", null, $"delegation-work:{d.Id:N}", CorrelationId: $"delegation-action:{d.Id:N}") { StartInBacklog = true }, token);
                    var work = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id, token);
                    work.IsExecutable = false; // Executed through the shared, leased delegation callback, never a second personal task runner.
                    work.SourceConversationId = d.RecipientConversationId;
                    work.SourceMessageId = d.RecipientMessageId;
                    d.WorkItemId = item.Id;

                }
                if (d.TrackingCancelledAt is null)
                {
                    await BindSubscription(d, actor, token);
                    await BindSubscription(d, d.CoordinatorId, token);
                }
                d.Status = "Active";
                var bound = await RequireItem(organization, actor, d.WorkItemId!.Value, false, token);
                if (bound.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)
                    Wake(organization, await Actor(organization, actor, token), DelegationCapabilities.Changed,
                        new WorkTrackingWake(bound.Id, d.Id), $"delegation-binding-review:{d.Id:N}:{bound.Id:N}");
                // Use the normal persisted turn and lease path, including current execution grants.
                if (d.ExecutionTurnId is null)
                {
                    var started = await turns.StartForAgentAsync(organization, d.RecipientConversationId, actor,
                        d.Instruction, d.CoordinatorId, idempotencyKey: $"delegation-execution:{d.Id:N}", cancellationToken: token)
                        ?? throw new InvalidOperationException("The delegated execution turn could not be created.");
                    d.ExecutionTurnId = started.Turn.Id;
                    d.ExecutionMessageId = started.UserMessage.Id;
                }
            }
            else if (request.Disposition is "Completed" or "Failed")
            {
                if (d.WorkItemId is not { } linked) throw new InvalidOperationException("A final result requires linked work.");
                var item = await RequireItem(organization, actor, linked, false, token);
                if (item.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
                {
                    if (item.CorrelationId != $"delegation-action:{d.Id:N}" || item.IsExecutable || d.ExecutionTurnId is not { } execution ||
                        !await db.ChatTurns.AnyAsync(x => x.Id == execution && x.OrganizationId == organization && x.TargetAgentOrganizationUserId == actor &&
                            (x.Status == ChatTurnStatus.Completed || x.Status == ChatTurnStatus.CompletedWithWarnings) && x.AssistantMessageId != null, token))
                        throw new InvalidOperationException("The linked work has no authoritative terminal outcome.");
                    item.Status = request.Disposition == "Completed" ? WorkTaskStatus.Completed : WorkTaskStatus.Cancelled;
                    item.Revision++; item.UpdatedAt = Now; item.ResultSummary = request.Summary;
                }
                if (request.Disposition == "Completed" && item.Status != WorkTaskStatus.Completed ||
                    request.Disposition == "Failed" && item.Status != WorkTaskStatus.Cancelled)
                    throw new InvalidOperationException("The reported outcome contradicts the authoritative work status.");
                outcomeItem = item;
                d.Status = d.TrackingCancelledAt is null ? "ResultReady" : "Completed";
            }
            else d.Status = request.Disposition switch { "Clarification" => "AwaitingClarification", "Declined" => "Declined", "Blocked" => "Blocked", _ => "Active" };
            d.Revision++; d.UpdatedAt = Now; d.AttentionRaisedAt = null;
            d.LastSubstantiveAt = Now;
            var update = AddUpdate(organization, actor, d.WorkItemId ?? Guid.Empty, d.Id, d.Revision,
                request.Disposition, request.Summary, request.IdempotencyKey, Hash(request with { ExpectedRevision = 0 }));
            if (outcomeItem is not null) await Receipt(organization, Guid.Empty, "result-evidence", $"result-evidence:{update.Id:N}",
                outcomeItem.Id, update.Id, Hash(new { outcomeItem.Id, outcomeItem.Revision, outcomeItem.Status }), token);
            if (d.TrackingCancelledAt is null) Message(d.RecipientConversationId, actor, request.Summary + (d.WorkItemId is null ? "" : $"\n\n[Track work]({TrackingUrl(organization, d.WorkItemId.Value)})"),
                $"delegation-ack:{update.Id:N}", d.Id);
            await QueueUpdate(update, token);
            await db.SaveChangesAsync(token);
            return await Map(d, token);
        }, token);

    public Task<DelegationSnapshot> PublishAsync(Guid organization, Guid actor, PublishDelegationReportRequest request, CancellationToken token = default) =>
        Mutate(async () =>
        {
            Key(request.IdempotencyKey); Text(request.Summary, 1500);
            var d = await Load(organization, actor, request.DelegationId, token);
            if (d.CoordinatorId != actor || d.TrackingCancelledAt != null) throw new UnauthorizedAccessException();
            await RequireSource(d, token);
            var update = await db.WorkTrackingUpdates.SingleOrDefaultAsync(x => x.Id == request.UpdateId &&
                x.OrganizationId == organization && x.DelegationId == d.Id, token) ?? throw new KeyNotFoundException();
            if (update.ReportedAt is not null) return await Map(d, token);
            if (d.Status is "Completed" or "Cancelled") throw new InvalidOperationException("Tracking has already closed.");
            if (update.AvailableAt > Now) throw new InvalidOperationException("The progress report is not due.");
            if (update.Kind is "Completed" or "Failed")
            {
                var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == d.WorkItemId && x.OrganizationId == organization && x.ArchivedAt == null, token);
                var evidence = await db.WorkTrackingOperationReceipts.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                    x.Operation == "result-evidence" && x.UpdateId == update.Id, token);
                if (d.Status != "ResultReady" || item is null || update.WorkItemId != item.Id || evidence?.RequestHash != Hash(new { item.Id, item.Revision, item.Status }))
                {
                    await Receipt(organization, actor, "superseded-report", $"superseded-report:{update.Id:N}", update.WorkItemId, update.Id, "", token);
                    if (d.Status == "ResultReady") { d.Status = "Active"; d.Revision++; d.UpdatedAt = Now; }
                    Wake(organization, await Actor(organization, d.RecipientId, token), DelegationCapabilities.Changed,
                        new WorkTrackingWake(d.WorkItemId ?? Guid.Empty, d.Id), $"delegation-recheck-result:{update.Id:N}");
                    return await Map(d, token);
                }
            }
            var sourceSender = await db.CoreConversationMessages.Where(x => x.Id == d.SourceMessageId).Select(x => x.SenderOrganizationUserId).SingleAsync(token);
            if (!await db.ConversationParticipants.AnyAsync(x => x.ConversationId == d.ReportingConversationId &&
                x.OrganizationUserId == sourceSender && x.LeftAt == null, token)) throw new UnauthorizedAccessException();
            var content = request.Summary + (d.WorkItemId is null ? "" : $"\n\n[Track work]({TrackingUrl(organization, d.WorkItemId.Value)})");
            var message = Message(d.ReportingConversationId, actor, content, $"delegation-report:{update.Id:N}", d.Id);
            update.ReportedAt = Now; update.ReportMessageId = message.Id;
            // Publishing a newer snapshot covers earlier coalesced progress, not unrelated final outcomes.
            foreach (var older in await db.WorkTrackingUpdates.Where(x => x.DelegationId == d.Id && x.ReportedAt == null &&
                         x.OccurredAt <= update.OccurredAt && x.Kind == "Progress").ToListAsync(token)) older.ReportedAt = Now;
            foreach (var partyId in Parties(d).Where(x => x != sourceSender && x != actor && x != d.RecipientId))
            {
                var party = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == partyId && x.IsActive && x.ArchivedAt == null, token);
                if (party is null) continue;
                var chat = await DirectChat(organization, actor, partyId, token);
                Message(chat.Id, actor, content, $"delegation-report:{update.Id:N}:{partyId:N}", d.Id);
                if (party.EmployeeType == EmployeeType.Agent)
                    Wake(organization, party, DelegationCapabilities.Changed, new WorkTrackingWake(d.WorkItemId ?? Guid.Empty, d.Id), $"delegation-observer:{update.Id:N}:{partyId:N}");
            }
            if (d.ParentDelegationId is { } parentId)
            {
                var parent = await db.TrackedDelegations.SingleAsync(x => x.Id == parentId && x.OrganizationId == organization, token);
                if (!Closed(parent.Status) && parent.TrackingCancelledAt is null)
                {
                    var upstream = AddUpdate(organization, actor, parent.WorkItemId ?? Guid.Empty, parent.Id, parent.Revision,
                        update.Kind is "Completed" or "Failed" ? "Milestone" : update.Kind == "Declined" ? "Blocked" : update.Kind, request.Summary,
                        $"delegation-upstream:{update.Id:N}", Hash(new { update.Id, request.Summary }));
                    await QueueUpdate(upstream, token);
                    if (update.Kind is "Completed" or "Failed")
                        Wake(organization, await Actor(organization, parent.RecipientId, token), DelegationCapabilities.Changed,
                            new WorkTrackingWake(parent.WorkItemId ?? Guid.Empty, parent.Id), $"delegation-child-review:{update.Id:N}:{parent.Id:N}");
                }
            }
            if (update.Kind is "Completed" or "Failed" or "Declined")
            {
                if (d.Status == "ResultReady") d.Status = "Completed";
                await CloseCommitment(d, token);
                foreach (var sub in await db.WorkTrackingSubscriptions.Where(x => x.DelegationId == d.Id).ToListAsync(token)) sub.UnfollowedAt = Now;
            }
            d.Revision++; d.UpdatedAt = Now;
            await db.SaveChangesAsync(token);
            return await Map(d, token);
        }, token);

    public Task<DelegationSnapshot> CancelAsync(Guid organization, Guid actor, CancelDelegationRequest request, CancellationToken token = default) =>
        Mutate(async () =>
        {
            Key(request.IdempotencyKey);
            var d = await Load(organization, actor, request.DelegationId, token);
            if (actor != d.CoordinatorId && actor != d.OriginalRequesterId) throw new UnauthorizedAccessException();
            if (d.TrackingCancelledAt is not null) return await Map(d, token);
            if (Closed(d.Status) || d.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException();
            var descendants = new List<TrackedDelegation> { d };
            for (var i = 0; i < descendants.Count; i++)
            {
                if (descendants.Count > 64) throw new InvalidOperationException("Delegation chain exceeds the recovery bound.");
                var current = descendants[i];
                descendants.AddRange(await db.TrackedDelegations.Where(x => x.ParentDelegationId == current.Id &&
                    x.OrganizationId == organization && x.TrackingCancelledAt == null && x.Status != "Completed" && x.Status != "Declined").ToListAsync(token));
                current.TrackingCancelledAt = Now; current.Revision++; current.UpdatedAt = Now;
                await CloseCommitment(current, token);
                foreach (var sub in await db.WorkTrackingSubscriptions.Where(x => x.DelegationId == current.Id).ToListAsync(token)) sub.UnfollowedAt = Now;
                Message(current.ReportingConversationId, current.CoordinatorId, "Delegation tracking cancelled. Existing work remains under its current owner.",
                    $"delegation-cancelled:{current.Id:N}", current.Id);
            }
            await db.SaveChangesAsync(token);
            return await Map(d, token);
        }, token);

    public Task<WorkTrackingSnapshot> FollowAsync(Guid organization, Guid actor, FollowWorkRequest request, bool follow, CancellationToken token = default) =>
        Mutate(async () =>
        {
            Key(request.IdempotencyKey); await Actor(organization, actor, token);
            if (follow) await RequireItem(organization, actor, request.WorkItemId, false, token);
            var operation = follow ? "follow" : "unfollow";
            if (await Receipt(organization, actor, operation, request.IdempotencyKey, request.WorkItemId, null, Hash(request), token))
                return await FollowResult(organization, actor, request.WorkItemId, follow, token);
            var sub = await db.WorkTrackingSubscriptions.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                x.WorkItemId == request.WorkItemId && x.FollowerId == actor && x.DelegationId == null, token);
            if (!follow && sub is null) throw new UnauthorizedAccessException();
            if (sub is null && follow)
            {
                sub = new() { Id = Guid.NewGuid(), OrganizationId = organization, WorkItemId = request.WorkItemId, FollowerId = actor, CreatedAt = Now, ProcessedThrough = Now };
                db.WorkTrackingSubscriptions.Add(sub);
            }
            if (sub is not null) sub.UnfollowedAt = follow ? null : Now;
            await db.SaveChangesAsync(token);
            return await FollowResult(organization, actor, request.WorkItemId, follow, token);
        }, token);

    private async Task<WorkTrackingSnapshot> FollowResult(Guid organization, Guid actor, Guid item, bool follow, CancellationToken token)
    {
        try { return await ReadTrackingAsync(organization, actor, item, token); }
        catch (Exception error) when (!follow && error is UnauthorizedAccessException or KeyNotFoundException)
        { return new(item, false, true, null, []); }
    }

    public async Task<WorkTrackingSnapshot> ReadTrackingAsync(Guid organization, Guid actor, Guid item, CancellationToken token = default)
    {
        await Actor(organization, actor, token);
        var full = await CanReadItem(organization, actor, item, token);
        var delegations = await db.TrackedDelegations.Where(x => x.OrganizationId == organization && x.WorkItemId == item).ToListAsync(token);
        var accessible = new List<Guid>();
        foreach (var d in delegations.Where(x => Participant(x, actor)))
        {
            try { await RequireSource(d, token); accessible.Add(d.Id); }
            catch (UnauthorizedAccessException) { }
        }
        var visible = accessible.ToArray();
        if (!full && visible.Length == 0) throw new UnauthorizedAccessException();
        var updates = await db.WorkTrackingUpdates.AsNoTracking().Where(x => x.OrganizationId == organization && x.WorkItemId == item &&
            (x.DelegationId == null && full || x.DelegationId != null && visible.Contains(x.DelegationId.Value)))
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id).Take(50).ToListAsync(token);
        var following = await db.WorkTrackingSubscriptions.AnyAsync(x => x.OrganizationId == organization && x.WorkItemId == item &&
            x.FollowerId == actor && x.DelegationId == null && x.UnfollowedAt == null, token);
        return new(item, following, !full, full ? await ItemUrl(organization, item, token) : null, updates.Select(MapUpdate).ToArray());
    }

    public Task<TrackingUpdate> ProgressAsync(Guid organization, Guid actor, WorkProgressRequest request, CancellationToken token = default) =>
        Mutate(async () =>
        {
            Key(request.IdempotencyKey); Text(request.Summary);
            var item = await RequireItem(organization, actor, request.WorkItemId, true, token);
            var existing = await db.WorkTrackingUpdates.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                x.AuthorId == actor && x.IdempotencyKey == request.IdempotencyKey, token);
            if (existing is not null)
            {
                if (existing.RequestHash != Hash(request)) throw new DbUpdateConcurrencyException();
                return MapUpdate(existing);
            }
            if (item.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Work changed.");
            if (request.Kind is not ("Progress" or "Milestone" or "Blocked" or "Decision" or "Completed" or "Failed")) throw new ArgumentException("Unknown update kind.");
            if (request.Kind is "Completed" or "Failed" && item.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
                throw new InvalidOperationException("A final report requires terminal work evidence.");
            if (request.Kind == "Completed" && item.Status != WorkTaskStatus.Completed || request.Kind == "Failed" && item.Status != WorkTaskStatus.Cancelled)
                throw new InvalidOperationException("The reported outcome contradicts the authoritative work status.");
            if (request.DelegationId is { } id)
            {
                var d = await Load(organization, actor, id, token);
                if (d.RecipientId != actor || d.WorkItemId != item.Id || Closed(d.Status)) throw new UnauthorizedAccessException();
                await RequireSource(d, token);
                if (request.Kind is "Completed" or "Failed") d.Status = d.TrackingCancelledAt is null ? "ResultReady" : "Completed";
                else if (request.Kind == "Blocked") d.Status = "Blocked";
                d.UpdatedAt = Now; d.Revision++; d.AttentionRaisedAt = null;
                d.LastSubstantiveAt = Now;
            }
            var update = AddUpdate(organization, actor, item.Id, request.DelegationId, item.Revision,
                request.Kind, request.Summary, request.IdempotencyKey, Hash(request));
            if (request.Kind is "Completed" or "Failed") await Receipt(organization, Guid.Empty, "result-evidence", $"result-evidence:{update.Id:N}",
                item.Id, update.Id, Hash(new { item.Id, item.Revision, item.Status }), token);
            await QueueUpdate(update, token);
            return MapUpdate(update);
        }, token);

    public async Task<IReadOnlyList<PendingTrackingUpdate>> PendingAsync(Guid organization, Guid actor, int limit, CancellationToken token = default)
    {
        await Actor(organization, actor, token);
        if (limit is < 1 or > 50) throw new ArgumentException("Limit must be 1 through 50.");
        var candidates = await db.WorkTrackingUpdates.AsNoTracking().Where(x => x.OrganizationId == organization && x.AvailableAt <= Now &&
            (x.Kind != "Progress" || !db.WorkTrackingUpdates.Any(y => y.OrganizationId == organization && y.DelegationId == x.DelegationId &&
                y.WorkItemId == x.WorkItemId && y.Kind == "Progress" && y.AvailableAt <= Now &&
                (y.OccurredAt > x.OccurredAt || y.OccurredAt == x.OccurredAt && y.Id.CompareTo(x.Id) > 0))) &&
            x.ReportedAt == null && x.DelegationId != null && db.TrackedDelegations.Any(d => d.Id == x.DelegationId &&
                d.OrganizationId == organization && d.CoordinatorId == actor && d.TrackingCancelledAt == null && d.Status != "Cancelled" && d.Status != "Completed" && d.Status != "AttentionRequired") &&
            !db.WorkTrackingOperationReceipts.Any(r => r.OrganizationId == organization && r.Operation == "superseded-report" && r.UpdateId == x.Id))
            .OrderBy(x => x.Kind == "Progress").ThenBy(x => x.OccurredAt).Take(limit).ToListAsync(token);
        var independent = await db.WorkTrackingUpdates.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.DelegationId == null && x.AvailableAt <= Now && db.WorkTrackingSubscriptions.Any(s => s.OrganizationId == organization &&
                s.FollowerId == actor && s.WorkItemId == x.WorkItemId && s.DelegationId == null && s.UnfollowedAt == null && s.CreatedAt <= x.OccurredAt) &&
            !db.WorkTrackingOperationReceipts.Any(r => r.OrganizationId == organization && r.ActorId == actor && r.Operation == "processed" && r.UpdateId == x.Id) &&
            (x.Kind != "Progress" || !db.WorkTrackingUpdates.Any(y => y.OrganizationId == organization && y.DelegationId == null &&
                y.WorkItemId == x.WorkItemId && y.Kind == "Progress" && y.AvailableAt <= Now &&
                (y.OccurredAt > x.OccurredAt || y.OccurredAt == x.OccurredAt && y.Id.CompareTo(x.Id) > 0))))
            .OrderBy(x => x.Kind == "Progress").ThenBy(x => x.OccurredAt).Take(limit * 4).ToListAsync(token);
        foreach (var update in independent)
        {
            if (await CanReadItem(organization, actor, update.WorkItemId, token)) candidates.Add(update);
        }
        return candidates.OrderBy(x => x.Kind == "Progress").ThenBy(x => x.OccurredAt).Take(limit).Select(x => new PendingTrackingUpdate(x.DelegationId, x.WorkItemId, MapUpdate(x))).ToArray();
    }

    public Task<WorkTrackingSnapshot> ProcessedAsync(Guid organization, Guid actor, ProcessTrackingUpdateRequest request, CancellationToken token = default) =>
        Mutate(async () =>
        {
            await RequireItem(organization, actor, request.WorkItemId, false, token);
            var update = await db.WorkTrackingUpdates.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
                x.WorkItemId == request.WorkItemId && x.Id == request.UpdateId && x.DelegationId == null, token) ?? throw new KeyNotFoundException();
            if (!await db.WorkTrackingSubscriptions.AnyAsync(x => x.OrganizationId == organization && x.WorkItemId == request.WorkItemId &&
                x.FollowerId == actor && x.DelegationId == null && x.UnfollowedAt == null, token)) throw new UnauthorizedAccessException();
            await Receipt(organization, actor, "processed", $"processed:{update.Id:N}", request.WorkItemId, update.Id, Hash(request), token);
            return await ReadTrackingAsync(organization, actor, request.WorkItemId, token);
        }, token);

    private async Task<bool> Receipt(Guid org, Guid actor, string operation, string key, Guid item, Guid? update, string hash, CancellationToken token)
    {
        var existing = db.WorkTrackingOperationReceipts.Local.SingleOrDefault(x => x.OrganizationId == org && x.ActorId == actor && x.Operation == operation && x.IdempotencyKey == key) ??
            await db.WorkTrackingOperationReceipts.SingleOrDefaultAsync(x => x.OrganizationId == org && x.ActorId == actor && x.Operation == operation && x.IdempotencyKey == key, token);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw new DbUpdateConcurrencyException("The operation input changed.");
            return true;
        }
        db.WorkTrackingOperationReceipts.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, ActorId = actor, Operation = operation,
            IdempotencyKey = key, RequestHash = hash, WorkItemId = item, UpdateId = update, CreatedAt = Now });
        return false;
    }

    public async Task ReconcileAsync(CancellationToken token = default)
    {
        var active = await db.TrackedDelegations.Where(x => x.Status != "Cancelled" && x.Status != "Completed" && x.Status != "Declined")
            .OrderBy(x => x.ReconciledAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        foreach (var d in active)
        {
            d.ReconciledAt = Now;
            var people = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == d.OrganizationId &&
                (x.Id == d.CoordinatorId || x.Id == d.RecipientId)).ToListAsync(token);
            var inactive = people.Count != 2 || people.Any(x => !x.IsActive || x.ArchivedAt != null);
            var changedSource = false;
            try { await RequireSource(d, token); } catch (UnauthorizedAccessException) { changedSource = true; }
            if ((inactive || changedSource) && d.Status != "AttentionRequired")
            {
                d.Status = "AttentionRequired"; d.Revision++; d.UpdatedAt = Now;
                Attention.Add(1, new KeyValuePair<string, object?>("condition", "responsibility-unavailable"));
                var managerIds = people.Select(x => x.ReportsToOrganizationUserId).Where(x => x != null).Select(x => x!.Value).Append(d.OriginalRequesterId).Distinct();
                foreach (var id in managerIds)
                {
                    var manager = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == d.OrganizationId && x.IsActive, token);
                    if (manager is not null) Notify(manager, d.OrganizationId, "Delegation needs attention", "A delegation participant or instruction source is no longer available.",
                        $"/organizations/{d.OrganizationId:D}/communications", $"delegation-attention:{d.Id:N}");
                }
            }
            if (d.Status == "AttentionRequired") continue;
            if (d.ExecutionTurnId is { } turnId)
            {
                var turn = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turnId && x.OrganizationId == d.OrganizationId, token);
                if (turn?.Status is ChatTurnStatus.Completed or ChatTurnStatus.CompletedWithWarnings or ChatTurnStatus.Failed or ChatTurnStatus.Cancelled &&
                    !await Receipt(d.OrganizationId, Guid.Empty, "execution-review", $"execution-review:{turnId:N}", d.WorkItemId ?? Guid.Empty, null, "", token))
                {
                    if (turn.Status is ChatTurnStatus.Failed or ChatTurnStatus.Cancelled)
                    {
                        d.Status = "Blocked"; d.Revision++;
                        var failed = AddUpdate(d.OrganizationId, Guid.Empty, d.WorkItemId ?? Guid.Empty, d.Id, d.Revision,
                            "Blocked", "The recipient's execution turn did not finish successfully. Review or retry is needed.", $"delegation-turn-failed:{turnId:N}", "");
                        await QueueUpdate(failed, token);
                    }
                    else
                    {
                        var recipient = await Actor(d.OrganizationId, d.RecipientId, token);
                        Wake(d.OrganizationId, recipient, DelegationCapabilities.Changed, new WorkTrackingWake(d.WorkItemId ?? Guid.Empty, d.Id),
                            $"delegation-execution-review:{turnId:N}");
                    }
                }
            }
            if (d.TrackingCancelledAt is null && d.AttentionRaisedAt is null && Now - d.LastSubstantiveAt >= (d.Status == "AwaitingAcknowledgement" ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24)))
            {
                d.AttentionRaisedAt = Now;
                Attention.Add(1, new KeyValuePair<string, object?>("condition", d.Status == "AwaitingAcknowledgement" ? "acknowledgement" : "stale-progress"));
                var update = AddUpdate(d.OrganizationId, Guid.Empty, d.WorkItemId ?? Guid.Empty, d.Id, d.Revision,
                    "Attention", d.Status == "AwaitingAcknowledgement" ? "Acknowledgement is still pending." : "No substantive progress has been reported in 24 hours.",
                    $"delegation-stale:{d.Id:N}:{d.Revision}", "");
                await QueueUpdate(update, token);
            }
        }
        var due = await db.WorkTrackingUpdates.Where(x => x.NotifiedAt == null && x.AvailableAt <= Now)
            .OrderBy(x => x.AvailableAt).Take(100).ToListAsync(token);
        foreach (var update in due)
        {
            // Automatic status changes wake current subscribers without copying the private task body.
            if (update.DelegationId is null)
            {
                var related = await db.TrackedDelegations.Where(x => x.OrganizationId == update.OrganizationId && x.WorkItemId == update.WorkItemId &&
                    x.Status != "Completed" && x.Status != "Cancelled" && x.Status != "Declined" && x.Status != "AttentionRequired").ToListAsync(token);
                foreach (var d in related)
                {
                    var key = $"delegation-status:{update.Id:N}:{d.Id:N}";
                    if (!await db.WorkTrackingUpdates.AnyAsync(x => x.OrganizationId == update.OrganizationId && x.AuthorId == Guid.Empty && x.IdempotencyKey == key, token))
                    {
                        if (update.OccurredAt > d.LastSubstantiveAt) { d.LastSubstantiveAt = update.OccurredAt; d.AttentionRaisedAt = null; }
                        var projected = AddUpdate(update.OrganizationId, Guid.Empty, update.WorkItemId, d.Id, update.SourceRevision,
                            update.Kind, update.Summary, key, "");
                        await QueueUpdate(projected, token);
                    }
                    var receiver = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == d.RecipientId && x.IsActive, token);
                    if (receiver is not null && update.Kind == "WorkTerminal")
                        Wake(d.OrganizationId, receiver, DelegationCapabilities.Changed, new WorkTrackingWake(update.WorkItemId, d.Id), $"delegation-result-review:{update.Id:N}:{d.Id:N}");
                }
            }
            await QueueUpdate(update, token);
        }
        await db.SaveChangesAsync(token);
    }

    private DomainUpdate AddUpdate(Guid org, Guid actor, Guid item, Guid? delegation, long revision, string kind, string summary, string key, string hash)
    {
        using var trace = Traces.StartActivity("tracking.receipt");
        trace?.SetTag("delegation.id", delegation); trace?.SetTag("work.id", item);
        trace?.SetTag("receipt.kind", kind); Receipts.Add(1, new KeyValuePair<string, object?>("kind", kind));
        var available = kind == "Progress" ? Now.AddMinutes(5) : Now;
        if (kind == "Progress")
        {
            var previous = db.WorkTrackingUpdates.Where(x => x.OrganizationId == org && x.WorkItemId == item && x.DelegationId == delegation &&
                x.Kind == "Progress" && x.NotifiedAt == null && x.AvailableAt > Now).Select(x => (DateTimeOffset?)x.AvailableAt).Min();
            available = previous ?? available;
        }
        var value = new DomainUpdate { Id = Guid.NewGuid(), OrganizationId = org, AuthorId = actor, WorkItemId = item,
            DelegationId = delegation, SourceRevision = revision, Kind = kind, Summary = summary, IdempotencyKey = key,
            RequestHash = hash, OccurredAt = Now, AvailableAt = available };
        trace?.SetTag("receipt.id", value.Id);
        if (delegation is { } id)
            trace?.SetTag("commitment.id", db.TrackedDelegations.Local.FirstOrDefault(x => x.Id == id)?.CommitmentItemId);
        db.WorkTrackingUpdates.Add(value); return value;
    }

    private async Task QueueUpdate(DomainUpdate update, CancellationToken token)
    {
        if (update.AvailableAt > Now || update.NotifiedAt != null) return;
        if (update.Kind == "Progress")
        {
            var newer = db.WorkTrackingUpdates.Local.Any(x => x.Id != update.Id && x.OrganizationId == update.OrganizationId &&
                x.WorkItemId == update.WorkItemId && x.DelegationId == update.DelegationId && x.Kind == "Progress" && x.AvailableAt <= Now &&
                x.OccurredAt > update.OccurredAt) || await db.WorkTrackingUpdates.AnyAsync(x => x.Id != update.Id &&
                    x.OrganizationId == update.OrganizationId && x.WorkItemId == update.WorkItemId && x.DelegationId == update.DelegationId &&
                    x.Kind == "Progress" && x.AvailableAt <= Now && x.OccurredAt > update.OccurredAt, token);
            if (newer) { update.NotifiedAt = Now; return; }
        }
        if (update.DelegationId is { } id)
        {
            var d = db.TrackedDelegations.Local.SingleOrDefault(x => x.Id == id) ?? await db.TrackedDelegations.SingleAsync(x => x.Id == id, token);
            var coordinator = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == d.CoordinatorId && x.IsActive, token);
            if (coordinator is not null && d.TrackingCancelledAt is null && d.Status is not ("Cancelled" or "Completed" or "AttentionRequired"))
                Wake(update.OrganizationId, coordinator, DelegationCapabilities.Changed, new WorkTrackingWake(update.WorkItemId, id), $"delegation-update:{update.Id:N}");
        }
        else
        {
            var subscriptions = await db.WorkTrackingSubscriptions.Where(x => x.OrganizationId == update.OrganizationId && x.WorkItemId == update.WorkItemId &&
                x.UnfollowedAt == null && x.DelegationId == null).ToListAsync(token);
            foreach (var sub in subscriptions)
            {
                if (!await CanReadItem(update.OrganizationId, sub.FollowerId, update.WorkItemId, token)) continue;
                var follower = await Actor(update.OrganizationId, sub.FollowerId, token);
                if (follower.EmployeeType == EmployeeType.Agent)
                    Wake(update.OrganizationId, follower, DelegationCapabilities.Changed, new WorkTrackingWake(update.WorkItemId), $"work-follow:{update.Id:N}:{follower.Id:N}");
                else Notify(follower, update.OrganizationId, "Followed work updated", update.Summary,
                    TrackingUrl(update.OrganizationId, update.WorkItemId), $"work-follow:{update.Id:N}:{follower.Id:N}");
            }
        }
        update.NotifiedAt = Now;
    }

    private async Task BindSubscription(TrackedDelegation d, Guid actor, CancellationToken token)
    {
        if (d.TrackingCancelledAt is not null || d.WorkItemId is not { } item) return;
        var old = await db.WorkTrackingSubscriptions.Where(x => x.DelegationId == d.Id && x.FollowerId == actor).ToListAsync(token);
        foreach (var sub in old.Where(x => x.WorkItemId != item)) sub.UnfollowedAt = Now;
        var current = old.SingleOrDefault(x => x.WorkItemId == item);
        if (current is not null) current.UnfollowedAt = null;
        else db.WorkTrackingSubscriptions.Add(new() { Id = Guid.NewGuid(), OrganizationId = d.OrganizationId,
            WorkItemId = item, FollowerId = actor, DelegationId = d.Id, CreatedAt = Now });
    }

    private async Task RequireSource(TrackedDelegation d, CancellationToken token)
    {
        await Actor(d.OrganizationId, d.CoordinatorId, token);
        await Actor(d.OrganizationId, d.RecipientId, token);
        var source = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == d.SourceMessageId &&
            x.ConversationId == d.SourceConversationId && x.Conversation!.OrganizationId == d.OrganizationId && x.Conversation.ArchivedAt == null, token);
        if (source is null || Hash(source.Content) != d.SourceChecksum) throw new UnauthorizedAccessException("The source instruction changed or is unavailable.");
        if (source.SenderOrganizationUserId is not { } sender) throw new UnauthorizedAccessException();
        await Actor(d.OrganizationId, sender, token);
        if (d.IsDirective && (!await IsAncestor(d.OrganizationId, d.CoordinatorId, d.RecipientId, token) ||
            sender != d.CoordinatorId && !await IsAncestor(d.OrganizationId, sender, d.CoordinatorId, token)))
            throw new UnauthorizedAccessException("The instruction's reporting authority changed.");
        if (!await db.ConversationParticipants.AnyAsync(x => x.ConversationId == d.ReportingConversationId &&
            x.OrganizationUserId == sender && x.LeftAt == null, token)) throw new UnauthorizedAccessException();
        if (!await db.ConversationParticipants.AnyAsync(x => x.ConversationId == d.ReportingConversationId &&
            x.OrganizationUserId == d.CoordinatorId && x.LeftAt == null, token)) throw new UnauthorizedAccessException();
    }

    private async Task<WorkTask> RequireItem(Guid org, Guid actor, Guid id, bool write, CancellationToken token)
    {
        var person = await Actor(org, actor, token);
        var item = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org &&
            x.ArchivedAt == null && x.Board!.ArchivedAt == null, token) ?? throw new KeyNotFoundException("Work item not found.");
        if (write && item.AssignedEmployeeId != actor && item.AccountableOrganizationUserId != actor) throw new UnauthorizedAccessException();
        if (item.Board!.Kind == WorkBoardKind.Personal)
        {
            if (!await IsAncestor(org, actor, item.Board.OwnerOrganizationUserId!.Value, token) && actor != item.Board.OwnerOrganizationUserId &&
                person.PermissionLevel != OrganizationPermissionLevel.Owner) throw new UnauthorizedAccessException();
            var kind = person.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
            var subject = person.AgentInstallationId ?? actor;
            var allowed = await new ScopedActionAuthorizationService(db).AuthorizeAsync(org, kind, subject, PersonalTodoActions.Read, GrantScopeKind.Board, item.BoardId!.Value, token);
            if (!allowed.Allowed) throw new UnauthorizedAccessException();
        }
        else
        {
            var kind = person.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
            var subject = person.AgentInstallationId ?? actor;
            var auth = new ScopedActionAuthorizationService(db);
            var allowed = await auth.AuthorizeAsync(org, kind, subject, WorkItemActions.Read, GrantScopeKind.WorkItem, id, token);
            if (!allowed.Allowed) allowed = await auth.AuthorizeAsync(org, kind, subject, WorkItemActions.Read, GrantScopeKind.Board, item.BoardId!.Value, token);
            if (!allowed.Allowed && item.Board.TeamId is { } team) allowed = await auth.AuthorizeAsync(org, kind, subject, WorkItemActions.Read, GrantScopeKind.Team, team, token);
            if (!allowed.Allowed) throw new UnauthorizedAccessException();
        }
        return item;
    }

    private async Task<bool> CanReadItem(Guid org, Guid actor, Guid id, CancellationToken token)
    { try { await RequireItem(org, actor, id, false, token); return true; } catch (Exception e) when (e is UnauthorizedAccessException or KeyNotFoundException) { return false; } }

    private async Task<bool> IsAncestor(Guid org, Guid ancestor, Guid employee, CancellationToken token)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == org && x.IsActive)
            .Select(x => new { x.Id, x.ReportsToOrganizationUserId }).ToDictionaryAsync(x => x.Id, token);
        var visited = new HashSet<Guid>();
        for (var id = employee; people.TryGetValue(id, out var person) && visited.Add(id) && visited.Count <= 64;)
        {
            if (person.ReportsToOrganizationUserId == ancestor) return true;
            if (person.ReportsToOrganizationUserId is not { } manager) break;
            id = manager;
        }
        return false;
    }

    private async Task<Conversation> DirectChat(Guid org, Guid sender, Guid recipient, CancellationToken token)
    {
        var key = Conversation.ParticipantKey(sender, recipient);
        var chat = db.CoreConversations.Local.SingleOrDefault(x => x.OrganizationId == org && x.DirectParticipantKey == key) ??
            await db.CoreConversations.SingleOrDefaultAsync(x => x.OrganizationId == org && x.DirectParticipantKey == key && x.ArchivedAt == null, token);
        if (chat is not null) return chat;
        var recipientPerson = await Actor(org, recipient, token);
        chat = new() { Id = Guid.NewGuid(), OrganizationId = org, DirectParticipantKey = key, InitiatedByOrganizationUserId = sender,
            AgentOrganizationUserId = recipientPerson.EmployeeType == EmployeeType.Agent ? recipient : sender,
            Kind = ConversationKind.DirectHumanAgent, IsPrivate = true, IsDeletionProtected = true, CreatedAt = Now, UpdatedAt = Now };
        chat.Participants.Add(new() { Id = Guid.NewGuid(), OrganizationUserId = sender, JoinedAt = Now });
        chat.Participants.Add(new() { Id = Guid.NewGuid(), OrganizationUserId = recipient, JoinedAt = Now });
        db.CoreConversations.Add(chat); return chat;
    }

    private ConversationMessage Message(Guid chat, Guid sender, string content, string key, Guid causation)
    {
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat, SenderOrganizationUserId = sender,
            Role = ConversationRole.Assistant, DeliveryIntent = CommunicationDeliveryIntent.Inform, Content = content,
            IdempotencyKey = key, CorrelationId = causation, CausationId = causation, SourceProvider = "InApp", CreatedAt = Now };
        db.CoreConversationMessages.Add(message); return message;
    }

    private void Wake(Guid org, OrganizationUser person, string type, object data, string key)
    {
        if (person.AgentInstallationId is not { } installation) return;
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TargetInstallationId = installation,
            EventType = type, DataJson = JsonSerializer.Serialize(data, Json), IdempotencyKey = key, OccurredAt = Now, NextAttemptAt = Now });
    }

    private void Notify(OrganizationUser person, Guid org, string title, string body, string uri, string key)
    {
        db.UserNotifications.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, RecipientOrganizationUserId = person.Id,
            Category = "WorkTracking", Severity = NotificationSeverity.Important, Title = title, Body = body,
            ActionUri = uri, DeduplicationKey = key, CreatedAt = Now });
    }

    private async Task CloseCommitment(TrackedDelegation d, CancellationToken token)
    {
        var item = await db.CoreWorkTasks.SingleAsync(x => x.Id == d.CommitmentItemId, token);
        item.Status = d.TrackingCancelledAt is not null ? WorkTaskStatus.Cancelled : WorkTaskStatus.Completed;
        item.BoardColumnId = await db.WorkBoardColumns.Where(x => x.BoardId == item.BoardId && x.Category == WorkBoardColumnCategory.Done)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token) ?? item.BoardColumnId;
        item.Revision++; item.UpdatedAt = Now; item.ResultSummary = "Delegation reporting finished.";
        item.ClaimEventId = null; item.ClaimExpiresAt = null;
    }

    public static string TrackingUrl(Guid org, Guid item) => $"/organizations/{org:D}/work/tracking/{item:D}";
    private async Task<string?> ItemUrl(Guid org, Guid id, CancellationToken token)
    {
        var item = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, token);
        if (item?.Board is null) return null;
        return item.Board.Kind == WorkBoardKind.Personal
            ? $"/organizations/{org:D}/employees/{item.Board.OwnerOrganizationUserId:D}?tab=personal-board&item={id:D}"
            : $"/organizations/{org:D}/work/boards/{item.BoardId:D}?item={id:D}";
    }

    private static TrackingUpdate MapUpdate(DomainUpdate x) => new(x.Id, x.WorkItemId, x.DelegationId, x.SourceRevision, x.Kind,
        x.Summary, x.OccurredAt, x.AvailableAt, x.ReportedAt != null);
    private async Task<DelegationSnapshot> Map(TrackedDelegation d, CancellationToken token, bool executionEvidence = false)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == d.OrganizationId &&
            (x.Id == d.CoordinatorId || x.Id == d.RecipientId)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, token);
        var updates = await db.WorkTrackingUpdates.AsNoTracking().Where(x => x.DelegationId == d.Id)
            .OrderByDescending(x => x.OccurredAt).Take(50).ToListAsync(token);
        DelegationExecutionEvidence? evidence = null;
        if (executionEvidence && d.ExecutionTurnId is { } turn)
        {
            var result = await db.ChatTurns.AsNoTracking().Include(x => x.AssistantMessage).SingleOrDefaultAsync(x => x.Id == turn &&
                x.OrganizationId == d.OrganizationId && x.TargetAgentOrganizationUserId == d.RecipientId, token);
            if (result?.AssistantMessage is { } message && result.Status is ChatTurnStatus.Completed or ChatTurnStatus.CompletedWithWarnings)
                evidence = new(turn, message.Id, result.Status.ToString(), message.Content);
        }
        return new(d.Id, d.Revision, d.OriginalRequesterId, d.CoordinatorId, d.RecipientId, people.GetValueOrDefault(d.CoordinatorId, "Coordinator"),
            people.GetValueOrDefault(d.RecipientId, "Recipient"), d.Instruction, d.IntendedOutcome, d.TrackingCancelledAt is null ? d.Status : "Cancelled", d.IsDirective,
            d.ReportingConversationId, d.RecipientConversationId, d.RecipientMessageId, d.WorkItemId,
            d.WorkItemId is { } item ? TrackingUrl(d.OrganizationId, item) : null, d.CommitmentItemId, d.ParentDelegationId,
            d.WorkContextJson is null ? null : JsonSerializer.Deserialize<DelegationContext>(d.WorkContextJson, Json), d.CreatedAt,
            Parties(d), updates.Select(MapUpdate).ToArray()) { ExecutionTurnId = d.ExecutionTurnId, ExecutionMessageId = d.ExecutionMessageId,
                TrackingCancelled = d.TrackingCancelledAt is not null, ExecutionStatus = d.Status,
                AuthoredByOrganizationUserId = d.AuthoredByOrganizationUserId, InstructionAuthorId = d.InstructionAuthorId, ExecutionEvidence = evidence,
                SourceConversationId = d.SourceConversationId, SourceMessageId = d.SourceMessageId, SourceRevisionHash = d.SourceChecksum,
                WorkBoardId = d.WorkItemId is { } work ? await db.CoreWorkTasks.Where(x => x.Id == work && x.OrganizationId == d.OrganizationId).Select(x => x.BoardId).SingleOrDefaultAsync(token) : null,
                ExecutionHandled = await db.WorkTrackingUpdates.AnyAsync(x => x.OrganizationId == d.OrganizationId &&
                    x.DelegationId == d.Id && x.IdempotencyKey == $"delegation-executed:{d.Id:N}", token) };
    }
}
