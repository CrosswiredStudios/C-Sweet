using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ChiefLeadershipService(CSweetDbContext db, TimeProvider clock,
    IExecutiveDecisionService decisions, ICommunicationHubService communication, IHiringService hiring) : IChiefLeadershipService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<OrganizationUser> ChiefAsync(Guid organization, Guid installation, CancellationToken token) =>
        await ChiefWorkCards.ChiefAsync(db, organization, installation, token)
        ?? throw new UnauthorizedAccessException("The installation is not the current responsible Chief of Staff.");

    private async Task<ChiefLeadershipAgenda> AgendaAsync(Guid organization, CancellationToken token)
    {
        var agenda = await db.ChiefLeadershipAgendas.SingleOrDefaultAsync(x => x.Id == organization, token);
        if (agenda is not null) return agenda;
        agenda = new() { Id = organization, UpdatedAt = clock.GetUtcNow() };
        db.ChiefLeadershipAgendas.Add(agenda);
        return agenda;
    }

    public async Task<PresentedChiefFocus> PresentAsync(Guid organization, Guid installation, PresentChiefFocusRequest request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileKey) || request.Domains is null || request.ConversationId == Guid.Empty || request.MessageId == Guid.Empty)
            throw new ArgumentException("A profile, domain catalog and source message/conversation are required.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organization, token);
        var chief = await ChiefAsync(organization, installation, token);
        if (await ReplayAsync<PresentedChiefFocus>(organization, installation, request.IdempotencyKey, request, token) is { } replay) return replay;
        var agenda = await AgendaAsync(organization, token);
        if (agenda.FocusRevision != request.ExpectedFocusRevision) throw new DbUpdateConcurrencyException("The company focus changed.");
        if (request.ProfileVersion < 1 || request.ProfileKey.Length > 80 ||
            !new[] { "general", "game-studio", "saas", "ecommerce", "professional-services", "media-content", "custom" }.Contains(request.ProfileKey) ||
            request.Domains.Count is < 2 or > 4 || request.Domains.Any(x => x is null) || request.Domains.Select(x => x.Id).Distinct().Count() != request.Domains.Count ||
            request.Domains.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || !ValidKey(x.Id) || string.IsNullOrWhiteSpace(x.Label) || x.Label.Length > 160 || string.IsNullOrWhiteSpace(x.Description) || x.Description.Length > 2048))
            throw new ArgumentException("A supported profile and two to four distinct bounded focus domains are required.");
        (ChatTurn Turn, ConversationMessage Message, OrganizationUser Owner)? explicitSource = null;
        if (request.OwnerTurnId is { } ownerTurn)
        {
            if (agenda.FocusId is not null || string.IsNullOrWhiteSpace(request.ExplicitDomainId) ||
                !request.Domains.Any(x => x.Id == request.ExplicitDomainId))
                throw new ArgumentException("Only a known initial owner priority can bypass the focus question.");
            explicitSource = await OwnerTurnAsync(organization, chief.Id, ownerTurn, token);
            if (explicitSource.Value.Turn.Status != ChatTurnStatus.Running || explicitSource.Value.Turn.ConversationId != request.ConversationId || explicitSource.Value.Message.Id != request.MessageId ||
                !string.Equals(explicitSource.Value.Message.Content.Trim().TrimEnd('.'), "Focus on " + request.ExplicitDomainId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The current owner message does not specify this priority.");
        }
        else if (request.ExplicitDomainId is not null) throw new ArgumentException("An authenticated owner turn is required for an initial priority.");
        if ((agenda.ProfileKey != request.ProfileKey || agenda.ProfileVersion != request.ProfileVersion) &&
            await db.ChiefCoverageRecords.AnyAsync(x => x.OrganizationId == organization &&
                (x.ExecutionStatus == "WaitingHire" || x.ExecutionStatus == "WaitingDecision" || x.ExecutionStatus == "Blocked"), token))
            throw new InvalidOperationException("Resolve or explicitly defer existing leadership obligations before changing the coverage profile.");
        if (agenda.FocusId is not null && (agenda.ProfileKey != request.ProfileKey || agenda.ProfileVersion != request.ProfileVersion))
        {
            agenda.FocusId = null; agenda.FocusRevision++; agenda.Revision++;
        }
        agenda.ProfileKey = request.ProfileKey; agenda.ProfileVersion = request.ProfileVersion;
        var rows = await db.ChiefCoverageRecords.Where(x => x.OrganizationId == organization &&
            x.ProfileKey == request.ProfileKey && x.ProfileVersion == request.ProfileVersion).ToListAsync(token);
        foreach (var domain in request.Domains)
        {
            var row = rows.SingleOrDefault(x => x.DomainId == domain.Id);
            if (row is null)
            {
                row = new() { Id = Guid.NewGuid(), OrganizationId = organization, ProfileKey = request.ProfileKey,
                    ProfileVersion = request.ProfileVersion, DomainId = domain.Id, Label = domain.Label,
                    Description = domain.Description, Priority = domain.Priority, UpdatedAt = clock.GetUtcNow() };
                db.ChiefCoverageRecords.Add(row); rows.Add(row);
            }
            await ProjectAsync(chief, row, token);
        }
        if (explicitSource is { } source)
        {
            await SaveFocusAsync(agenda, chief, source.Turn, source.Owner.Id, request.ExplicitDomainId!, null, null, token);
            var captured = new PresentedChiefFocus(null, request.MessageId, agenda.FocusRevision);
            Receipt(organization, installation, request.IdempotencyKey, request, captured);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return captured;
        }
        var card = await decisions.CreateAsync(new(organization, request.ConversationId, null, request.MessageId, installation,
            "Where would you like the company to focus first?", request.Domains.OrderBy(x => x.Priority)
                .Select(x => new CreateExecutiveDecisionOption(x.Id, x.Label, x.Description)).ToArray(),
            request.Domains.OrderBy(x => x.Priority).First().Id, request.IdempotencyKey), token);
        if (!card.Options.Select(x => x.Id).Order().SequenceEqual(request.Domains.Select(x => x.Id).Order()))
            throw new ArgumentException("The existing focus decision belongs to a different domain catalog.");
        db.ChiefFocusPrompts.Add(new() { Id = card.Id, OrganizationId = organization, InstallationId = installation,
            ConversationId = request.ConversationId, ProfileKey = request.ProfileKey, ProfileVersion = request.ProfileVersion,
            ExpectedFocusRevision = agenda.FocusRevision, IdempotencyKey = request.IdempotencyKey });
        var answeredTurn = await db.ExecutiveDecisions.Where(x => x.Id == card.Id && x.Status == ExecutiveDecisionStatus.Answered)
            .Select(x => x.NextChatTurnId).SingleOrDefaultAsync(token);
        var result = new PresentedChiefFocus(card.Id, request.MessageId, agenda.FocusRevision, answeredTurn);
        Receipt(organization, installation, request.IdempotencyKey, request, result);
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    public async Task<ChiefLeadershipSnapshot> CaptureAsync(Guid organization, Guid installation, CaptureChiefFocusRequest request, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organization, token);
        var chief = await ChiefAsync(organization, installation, token);
        if (await ReplayAsync<ChiefLeadershipSnapshot>(organization, installation, request.IdempotencyKey, request, token) is { } replay) return replay;
        var agenda = await AgendaAsync(organization, token);
        if (request.AnswerTurnId == Guid.Empty) throw new ArgumentException("An authenticated owner turn is required.");
        var source = await OwnerTurnAsync(organization, chief.Id, request.AnswerTurnId, token);
        if (agenda.FocusRevision != request.ExpectedFocusRevision) throw new DbUpdateConcurrencyException("The company focus changed.");
        var decision = await db.ExecutiveDecisions.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.RequestingInstallationId == installation && x.NextChatTurnId == request.AnswerTurnId &&
            x.Status == ExecutiveDecisionStatus.Answered && (request.DecisionId == null || x.Id == request.DecisionId), token);
        if (decision is not null)
        {
            var purpose = await db.ChiefFocusPrompts.SingleOrDefaultAsync(x => x.Id == decision.Id && x.OrganizationId == organization &&
                x.InstallationId == installation && x.ConversationId == source.Turn.ConversationId, token)
                ?? throw new ArgumentException("This immutable decision is not a leadership decision.");
            if (decision.AnsweredByOrganizationUserId != source.Owner.Id || purpose.Purpose == "Focus" && purpose.ExpectedFocusRevision != agenda.FocusRevision ||
                purpose.ProfileKey != agenda.ProfileKey || purpose.ProfileVersion != agenda.ProfileVersion)
                throw new ArgumentException("The leadership decision is stale or belongs to another scope.");
            if (purpose.AssessmentId is { } assessmentId)
            {
                var row = await db.ChiefCoverageRecords.SingleAsync(x => x.Id == assessmentId && x.OrganizationId == organization, token);
                if (row.DecisionId != decision.Id || row.ExecutionStatus != "WaitingDecision") throw new ArgumentException("The assessment no longer awaits this answer.");
                row.AnswerDecisionId = decision.Id;
                switch (decision.SelectedOptionId)
                {
                    case "owner":
                        row.AccountableOwnerId = source.Owner.Id; row.CoverageModel = "Owner"; row.CoverageStatus = "AdequatelyCovered";
                        row.ExecutionStatus = "Complete"; row.Rationale = "The owner explicitly accepted accountability for this domain."; break;
                    case "defer":
                        row.CoverageStatus = "Deferred"; row.ExecutionStatus = "Complete"; row.ReviewAt = clock.GetUtcNow().AddDays(30);
                        row.Rationale = "The owner deferred this domain for later review."; break;
                    case "external":
                        row.CoverageStatus = "NeedsCoverage"; row.ExecutionStatus = "Blocked"; row.BlockCode = "leadership.external_confirmation_required";
                        row.Rationale = "An external professional was requested; the engagement and coverage remain unverified."; break;
                    default:
                        row.ExecutionStatus = "Blocked"; row.BlockCode = "leadership.owner_detail_requires_review";
                        row.Rationale = "The owner's custom answer is retained in its immutable decision for review; no coverage or hiring was inferred."; break;
                }
                row.Revision++; row.UpdatedAt = clock.GetUtcNow(); agenda.Revision++;
                row.InputFingerprint = await InputsAsync(organization, token);
                await ProjectAsync(chief, row, token);
                await AdvanceAsync(agenda, chief, token);
            }
            else
            {
                if (await db.ChiefFocusRecords.AnyAsync(x => x.OrganizationId == organization && x.SourceDecisionId == decision.Id, token))
                    throw new ArgumentException("This focus decision has already been consumed.");
                var domain = decision.SelectedOptionId ?? "custom";
                if (domain != "custom" && !await db.ChiefCoverageRecords.AnyAsync(x => x.OrganizationId == organization &&
                    x.ProfileKey == agenda.ProfileKey && x.ProfileVersion == agenda.ProfileVersion && x.DomainId == domain, token))
                    throw new ArgumentException("The selected domain is not in the current agenda.");
                await SaveFocusAsync(agenda, chief, source.Turn, source.Owner.Id, domain, decision.FreeTextAnswer, decision.Id, token);
            }
        }
        else
        {
            // Direct priorities must be exact current owner instructions, never inferred from history or a model summary.
            if (source.Turn.Status != ChatTurnStatus.Running || request.ExplicitDomainId is null || !ValidKey(request.ExplicitDomainId) ||
                !string.Equals(source.Message.Content.Trim().TrimEnd('.'), "Focus on " + request.ExplicitDomainId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("No authenticated focus answer or explicit current focus instruction was found.");
            if (agenda.FocusId is { } priorId && await db.ChiefFocusRecords.AnyAsync(x => x.Id == priorId && x.EffectiveAt > source.Turn.CreatedAt, token))
                throw new ArgumentException("A historical message cannot override a newer focus decision.");
            if (!await db.ChiefCoverageRecords.AnyAsync(x => x.OrganizationId == organization && x.ProfileKey == agenda.ProfileKey &&
                x.ProfileVersion == agenda.ProfileVersion && x.DomainId == request.ExplicitDomainId, token))
                throw new ArgumentException("That domain is not in the configured agenda.");
            await SaveFocusAsync(agenda, chief, source.Turn, source.Owner.Id, request.ExplicitDomainId, null, null, token);
        }
        var result = await SnapshotAsync(agenda, chief, new(), token);
        Receipt(organization, installation, request.IdempotencyKey, request, result);
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private async Task SaveFocusAsync(ChiefLeadershipAgenda agenda, OrganizationUser chief, ChatTurn turn, Guid owner,
        string domain, string? custom, Guid? decisionId, CancellationToken token)
    {
        var focus = new ChiefFocusRecord { Id = Guid.NewGuid(), OrganizationId = agenda.Id, Revision = agenda.FocusRevision + 1,
            DomainId = domain, CustomText = custom, SourceDecisionId = decisionId, SourceTurnId = turn.Id, DecisionMakerId = owner,
            ConversationId = turn.ConversationId, EffectiveAt = clock.GetUtcNow(), SupersedesId = agenda.FocusId ??
                await db.ChiefFocusRecords.Where(x => x.OrganizationId == agenda.Id).OrderByDescending(x => x.Revision).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token) };
        db.ChiefFocusRecords.Add(focus); agenda.FocusId = focus.Id; agenda.FocusRevision = focus.Revision;
        agenda.Revision++; agenda.UpdatedAt = clock.GetUtcNow();
        var rows = await RowsAsync(agenda, token);
        var selected = rows.SingleOrDefault(x => x.DomainId == domain);
        if (selected is null && domain == "custom")
        {
            selected = new() { Id = Guid.NewGuid(), OrganizationId = agenda.Id, ProfileKey = agenda.ProfileKey,
                ProfileVersion = agenda.ProfileVersion, DomainId = "custom", Label = "Custom priority", Description = custom ?? "Custom priority",
                Priority = 0, UpdatedAt = clock.GetUtcNow(), ExecutionStatus = "Blocked", BlockCode = "leadership.custom_focus_requires_mapping",
                Rationale = "The custom priority is saved. Map its scope before any proactive coverage action." };
            db.ChiefCoverageRecords.Add(selected); rows.Add(selected);
        }
        foreach (var row in rows)
        {
            if (row.ExecutionStatus is "WaitingHire" or "WaitingDecision") continue; // Preserve running/approved commitments.
            if (row == selected && domain != "custom") { row.ExecutionStatus = "Ready"; row.CoverageStatus = "Unassessed"; row.BlockCode = null; }
            else if (row.ExecutionStatus is "Ready" or "Assessing") row.ExecutionStatus = "Backlog";
            row.Revision++; row.UpdatedAt = clock.GetUtcNow(); await ProjectAsync(chief, row, token);
        }
        await WakeAsync(agenda, chief, token);
    }

    public async Task<ChiefLeadershipSnapshot> ReadAsync(Guid organization, Guid installation, ReadChiefLeadershipRequest request, CancellationToken token = default)
    {
        if (request.PageSize is < 1 or > 100) throw new ArgumentException("Page size must be between 1 and 100.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organization, token);
        var chief = await ChiefAsync(organization, installation, token);
        var agenda = await AgendaAsync(organization, token);
        if (request.AnswerTurnId is null)
        {
            // Reconnect discovery repairs answers persisted before a lost callback. The
            // immutable native decision and owner turn still supply all authorization.
            var answered = await (from prompt in db.ChiefFocusPrompts
                join decision in db.ExecutiveDecisions on prompt.Id equals decision.Id
                where prompt.OrganizationId == organization && prompt.InstallationId == installation &&
                    decision.Status == ExecutiveDecisionStatus.Answered && decision.NextChatTurnId != null &&
                    (prompt.Purpose == "Focus" && prompt.ExpectedFocusRevision == agenda.FocusRevision &&
                        !db.ChiefFocusRecords.Any(x => x.SourceDecisionId == decision.Id) ||
                     prompt.AssessmentId != null && db.ChiefCoverageRecords.Any(x => x.Id == prompt.AssessmentId &&
                        x.DecisionId == decision.Id && x.ExecutionStatus == "WaitingDecision"))
                orderby decision.CreatedAt
                select new { decision.Id, Turn = decision.NextChatTurnId!.Value }).Take(4).ToListAsync(token);
            foreach (var answer in answered)
                await CaptureAsync(organization, installation, new(answer.Turn, agenda.FocusRevision,
                    $"chief-recover-answer:{answer.Id:N}", answer.Id), token);
        }
        var rows = await RowsAsync(agenda, token);
        // The previous Game Studio override could save a noncreative owner focus
        // while readying an untouched Director assessment instead. Repair only that
        // unscheduled state; an accepted result, native question or hire is retained.
        if (agenda.ProfileKey == "game-studio" && agenda.FocusId is { } savedFocus &&
            !rows.Any(x => x.ExecutionStatus is "WaitingHire" or "WaitingDecision" or "Blocked"))
        {
            var selectedDomain = await db.ChiefFocusRecords.Where(x => x.Id == savedFocus && x.OrganizationId == organization)
                .Select(x => x.DomainId).SingleAsync(token);
            var selected = rows.SingleOrDefault(x => x.DomainId == selectedDomain && x.ExecutionStatus == "Backlog" && x.AcceptedResultJson == null);
            var director = rows.SingleOrDefault(x => x.DomainId == "creative-product" && x.ExecutionStatus == "Ready" &&
                x.CoverageStatus == "Unassessed" && x.AcceptedResultJson == null && x.InputFingerprint == null &&
                x.RecommendationId == null && x.DecisionId == null);
            if (selectedDomain is "research" or "financial" or "legal" && selected is not null && director is not null)
            {
                director.ExecutionStatus = "Backlog"; director.Revision++; director.UpdatedAt = clock.GetUtcNow();
                selected.ExecutionStatus = "Ready"; selected.Revision++; selected.UpdatedAt = clock.GetUtcNow();
                agenda.Revision++; await WakeAsync(agenda, chief, token);
            }
        }
        var inputs = await InputsAsync(organization, token);
        foreach (var row in rows)
        {
            if (row.ExecutionStatus == "WaitingHire" && row.RecommendationId is { } recommendation)
            {
                var plan = await db.WorkforcePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recommendation && x.OrganizationId == organization, token);
                if (plan?.Status == ProposalStatus.Cancelled)
                {
                    row.ExecutionStatus = "Complete"; row.CoverageStatus = "Deferred";
                    row.Rationale = "The source recommendation was withdrawn. Coverage remains deferred until an explicit focus change or review.";
                }
                else if (plan?.Status == ProposalStatus.Approved && plan.FulfilledHeadcount >= plan.Headcount)
                { row.ExecutionStatus = "Ready"; row.CoverageStatus = "Unassessed"; row.Rationale = "Hiring fulfilled; reassess actual responsibility and capacity."; }
                if (row.ExecutionStatus != "WaitingHire") { row.Revision++; row.UpdatedAt = clock.GetUtcNow(); agenda.Revision++; }
            }
            await ProjectAsync(chief, row, token);
        }
        // One review per read, only when no active obligation needs attention. A changed
        // fingerprint is a wake hint, never proof that external work or a hire completed.
        if (!rows.Any(x => x.ExecutionStatus is "Ready" or "Assessing" or "WaitingHire" or "WaitingDecision"))
        {
            var review = rows.FirstOrDefault(x => x.ReviewAt <= clock.GetUtcNow() && x.ExecutionStatus == "Complete" ||
                x.InputFingerprint is not null && x.InputFingerprint != inputs &&
                (x.ExecutionStatus == "Blocked" && x.BlockCode == "leadership.evidence_missing" ||
                 x.ExecutionStatus == "Complete" && x.CoverageStatus is "AdequatelyCovered" or "InterimCoverage"));
            if (review is not null)
            {
                review.ExecutionStatus = "Ready"; review.CoverageStatus = "Unassessed"; review.BlockCode = null;
                review.ReviewAt = null; review.Revision++; review.UpdatedAt = clock.GetUtcNow(); agenda.Revision++;
                await ProjectAsync(chief, review, token); await WakeAsync(agenda, chief, token);
            }
        }
        await AdvanceAsync(agenda, chief, token);
        var result = await SnapshotAsync(agenda, chief, request, token);
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    public async Task<ChiefLeadershipSnapshot> ControlAsync(Guid organization, Guid installation, ControlChiefLeadershipRequest request, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organization, token);
        var chief = await ChiefAsync(organization, installation, token);
        if (await ReplayAsync<ChiefLeadershipSnapshot>(organization, installation, request.IdempotencyKey, request, token) is { } replay) return replay;
        var source = await OwnerTurnAsync(organization, chief.Id, request.OwnerTurnId, token);
        if (source.Turn.Status != ChatTurnStatus.Running || !string.Equals(source.Message.Content.Trim().TrimEnd('.'), request.Paused ? "Pause proactive leadership" : "Resume proactive leadership", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit current owner pause/resume instruction is required.");
        var agenda = await AgendaAsync(organization, token);
        if (agenda.FocusRevision != request.ExpectedFocusRevision) throw new DbUpdateConcurrencyException("The company focus changed.");
        agenda.Paused = request.Paused; agenda.Revision++; agenda.UpdatedAt = clock.GetUtcNow();
        foreach (var row in await RowsAsync(agenda, token)) await ProjectAsync(chief, row, token);
        if (!request.Paused) await WakeAsync(agenda, chief, token);
        var result = await SnapshotAsync(agenda, chief, new(), token);
        Receipt(organization, installation, request.IdempotencyKey, request, result);
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private async Task<(ChatTurn Turn, ConversationMessage Message, OrganizationUser Owner)> OwnerTurnAsync(Guid organization, Guid chief, Guid turnId, CancellationToken token)
    {
        var turn = await db.ChatTurns.SingleOrDefaultAsync(x => x.Id == turnId && x.OrganizationId == organization &&
            x.TargetAgentOrganizationUserId == chief && x.Status != ChatTurnStatus.Cancelled && x.Status != ChatTurnStatus.Failed, token)
            ?? throw new UnauthorizedAccessException("The turn is not addressed to the current Chief.");
        var message = await db.CoreConversationMessages.SingleAsync(x => x.Id == turn.UserMessageId && x.ConversationId == turn.ConversationId, token);
        var owner = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == message.SenderOrganizationUserId &&
            x.OrganizationId == organization && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner, token)
            ?? throw new UnauthorizedAccessException("Only an authenticated current business owner can set leadership focus.");
        return (turn, message, owner);
    }

    private Task<List<ChiefCoverageRecord>> RowsAsync(ChiefLeadershipAgenda agenda, CancellationToken token) =>
        db.ChiefCoverageRecords.Where(x => x.OrganizationId == agenda.Id && x.ProfileKey == agenda.ProfileKey && x.ProfileVersion == agenda.ProfileVersion)
            .OrderBy(x => x.Priority).ThenBy(x => x.Id).ToListAsync(token);

    private async Task ProjectAsync(OrganizationUser chief, ChiefCoverageRecord row, CancellationToken token)
    {
        var agenda = db.ChiefLeadershipAgendas.Local.SingleOrDefault(x => x.Id == chief.OrganizationId)
            ?? await AgendaAsync(chief.OrganizationId, token);
        var block = agenda.Paused ? "Proactive leadership is paused by the owner." : null;
        await new WorkItemMutationEngine(db, clock).ProjectChiefAssessmentAsync(chief, row, token, block);
    }

    private async Task AdvanceAsync(ChiefLeadershipAgenda agenda, OrganizationUser chief, CancellationToken token)
    {
        if (agenda.FocusId is null) return;
        var rows = await RowsAsync(agenda, token);
        if (rows.Any(x => x.ExecutionStatus is "Ready" or "Assessing" or "WaitingHire" or "WaitingDecision" or "Blocked")) return;
        var next = rows.FirstOrDefault(x => x.ExecutionStatus == "Backlog");
        if (next is null) return;
        next.ExecutionStatus = "Ready"; next.Revision++; next.UpdatedAt = clock.GetUtcNow();
        await ProjectAsync(chief, next, token); agenda.Revision++; await WakeAsync(agenda, chief, token);
    }

    private async Task WakeAsync(ChiefLeadershipAgenda agenda, OrganizationUser chief, CancellationToken token)
    {
        var key = $"chief-leadership:{agenda.Id:N}:{agenda.Revision}";
        if (db.AgentPlatformEventOutbox.Local.Any(x => x.IdempotencyKey == key) ||
            await db.AgentPlatformEventOutbox.AnyAsync(x => x.IdempotencyKey == key, token)) return;
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = agenda.Id, TargetInstallationId = chief.AgentInstallationId,
            EventType = ChiefLeadershipCapabilities.Changed, IdempotencyKey = key,
            DataJson = JsonSerializer.Serialize(new { organizationId = agenda.Id, focusRevision = agenda.FocusRevision }, Json),
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = clock.GetUtcNow(), OccurredAt = clock.GetUtcNow() });
    }

    private static bool ValidKey(string value) => value.Length is > 0 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    private async Task<T?> ReplayAsync<T>(Guid organization, Guid installation, string key, object request, CancellationToken token) where T : class
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160) throw new ArgumentException("A stable bounded operation key is required.");
        var receipt = await db.ChiefLeadershipReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization && x.InstallationId == installation && x.OperationKey == key, token);
        if (receipt is null) return null;
        if (receipt.RequestDigest != Digest(request)) throw new ArgumentException("The operation key was already used for different input.");
        return JsonSerializer.Deserialize<T>(receipt.ResultJson, Json) ?? throw new InvalidOperationException("The operation receipt is invalid.");
    }
    private void Receipt(Guid organization, Guid installation, string key, object request, object result) => db.ChiefLeadershipReceipts.Add(new()
    { Id = Guid.NewGuid(), OrganizationId = organization, InstallationId = installation, OperationKey = key,
        RequestDigest = Digest(request), ResultJson = JsonSerializer.Serialize(result, Json), CreatedAt = clock.GetUtcNow() });

    private static ChiefCoverageAssessment Map(ChiefCoverageRecord x) => new(x.Id, x.ProfileKey, x.ProfileVersion, x.DomainId, x.Label,
        x.Description, x.Priority, x.CoverageStatus, x.ExecutionStatus, x.Revision, x.AccountableOwnerId, x.CoverageModel,
        x.Rationale, x.NextAction, x.RecommendationId, x.WorkItemId, x.DecisionId, x.ReviewAt, x.BlockCode, x.InputFingerprint);
}
