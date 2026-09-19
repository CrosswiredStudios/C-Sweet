using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectIntakeService(CSweetDbContext db, TimeProvider clock, ProjectSetupService setup, ProjectWorkPolicy policy, CSweet.Application.Communications.IAgentCoordinationService coordination, CSweet.Application.Core.IHiringService hiring)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Task<OrganizationUser> ActorAsync(Guid org, Guid installation, CancellationToken ct) => db.CoreOrganizationUsers.SingleAsync(x =>
        x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct);
    private async Task<ProjectIntake> OwnedAsync(Guid org, Guid installation, Guid id, CancellationToken ct)
        => await db.ProjectIntakes.SingleOrDefaultAsync(x => x.OrganizationId == org && x.DeveloperInstallationId == installation && x.Id == id, ct)
        ?? throw new KeyNotFoundException("This project request does not belong to this agent.");
    private async Task<(ConversationMessage Message, ChatTurn Turn)> SourceAsync(Guid org, Guid developer, Guid conversation, Guid message, CancellationToken ct)
    {
        var turn = await db.ChatTurns.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == org && x.TargetAgentOrganizationUserId == developer && x.ConversationId == conversation && x.UserMessageId == message, ct)
            ?? throw new UnauthorizedAccessException("The source message was not addressed to this agent.");
        var source = await db.CoreConversationMessages.AsNoTracking().SingleAsync(x => x.Id == message && x.ConversationId == conversation, ct);
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == org && x.Id == source.SenderOrganizationUserId && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, ct))
            throw new UnauthorizedAccessException("Project intake requires a current human request.");
        return (source, turn);
    }
    public async Task<ProjectIntakeSummary> RetainAsync(Guid org, Guid installation, RetainProjectIntakeRequest request, CancellationToken ct)
    {
        var actor = await ActorAsync(org, installation, ct);
        var (source, turn) = await SourceAsync(org, actor.Id, request.ConversationId, request.SourceMessageId, ct);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 || string.IsNullOrWhiteSpace(request.Goal) || request.Goal.Length > 6000 || request.TicketOwner is not ("ask" or "self" or "human") || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("Provide bounded project details, ticket ownership and a stable request key.");
        var existing = await db.ProjectIntakes.SingleOrDefaultAsync(x => x.OrganizationId == org && x.DeveloperInstallationId == installation && (x.IdempotencyKey == request.IdempotencyKey || x.SourceMessageId == source.Id), ct);
        if (existing is not null) return Summary(existing);
        var now = clock.GetUtcNow();
        var intake = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = org, DeveloperId = actor.Id, DeveloperInstallationId = installation,
            RequestingHumanId = source.SenderOrganizationUserId!.Value, ConversationId = request.ConversationId, SourceMessageId = source.Id, SourceChatTurnId = turn.Id,
            OriginalRequest = source.Content, Name = request.Name.Trim(), Goal = request.Goal.Trim(), TicketOwner = request.TicketOwner, EnvironmentId = request.EnvironmentId,
            TeamId = await db.TeamMemberships.Where(x => x.OrganizationId == org && x.OrganizationUserId == actor.Id).Select(x => (Guid?)x.TeamId).SingleOrDefaultAsync(ct),
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now, UpdatedAt = now };
        db.ProjectIntakes.Add(intake); setup.QueueIntake(intake); await db.SaveChangesAsync(ct);
        return Summary(intake);
    }
    public async Task<ProjectIntakeSummary> ReadAsync(Guid org, Guid installation, Guid id, CancellationToken ct)
        => Summary(await OwnedAsync(org, installation, id, ct));
    public async Task<IReadOnlyList<ProjectIntakeSummary>> ListAsync(Guid org, Guid installation, CancellationToken ct)
        => (await db.ProjectIntakes.AsNoTracking().Where(x => x.OrganizationId == org && x.DeveloperInstallationId == installation && x.Status != "Cancelled" && x.Status != "Started")
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(ct)).Select(Summary).ToArray();
    public async Task<IReadOnlyList<ProjectCandidate>> DiscoverAsync(Guid org, Guid installation, Guid id, CancellationToken ct)
    {
        var intake = await OwnedAsync(org, installation, id, ct);
        var human = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == intake.RequestingHumanId && x.OrganizationId == org && x.IsActive, ct);
        var query = db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == org && (x.Status == WorkstreamStatus.Active || x.Status == WorkstreamStatus.Approved));
        if (human.PermissionLevel < OrganizationPermissionLevel.Manager)
            query = query.Where(x => x.AccountableManagerOrganizationUserId == human.Id || db.ProjectParticipants.Any(p => p.WorkstreamId == x.Id && p.OrganizationUserId == human.Id && p.RemovedAt == null));
        return await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100).Select(x => new ProjectCandidate(x.Id, x.Name, x.Outcome, x.AccountableManagerOrganizationUserId ?? Guid.Empty,
            db.WorkstreamTeamAssignments.Where(t => t.WorkstreamId == x.Id && t.EndsAt == null).Select(t => (Guid?)t.TeamId).FirstOrDefault(),
            db.ProjectParticipants.Any(p => p.WorkstreamId == x.Id && p.OrganizationUserId == intake.DeveloperId && p.RemovedAt == null), x.Revision)).ToListAsync(ct);
    }
    public async Task<ProjectIntakeSummary> ChooseAsync(Guid org, Guid installation, ChooseProjectIntakeRequest request, CancellationToken ct)
    {
        var intake = await OwnedAsync(org, installation, request.IntakeId, ct);
        var (source, _) = await SourceAsync(org, intake.DeveloperId, intake.ConversationId, request.SourceMessageId, ct);
        if (source.SenderOrganizationUserId != intake.RequestingHumanId) throw new UnauthorizedAccessException("Only the requesting human can change this intake.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200) throw new ArgumentException("A stable choice key is required.");
        if (intake.LastChoiceKey == request.IdempotencyKey) return Summary(intake);
        if (intake.Revision != request.ExpectedRevision || intake.Status is "Cancelled" or "Started") throw new DbUpdateConcurrencyException("The project request changed. Read it again before applying this choice.");
        if (request.Choice is "self" or "human") intake.TicketOwner = request.Choice;
        else
        {
            if (request.Choice is not ("create" or "existing" or "manager" or "cancel")) throw new ArgumentException("Choose create, existing, manager, cancel, self or human.");
            await CancelObsoleteAssistanceAsync(intake, ct);
            // A changed choice invalidates the old setup link and any manager reservation.
            foreach (var reservation in await db.ProjectManagerReservations.Where(x => x.IntakeId == intake.Id && x.WorkstreamId == null).ToListAsync(ct)) db.ProjectManagerReservations.Remove(reservation);
            intake.ProjectId = null; intake.BoardId = null; intake.ManagerId = null; intake.Issue = null;
            intake.ChiefId = null; intake.CoordinationSessionId = null; intake.HiringRecommendationId = null;
            intake.TeamId = await db.TeamMemberships.Where(x => x.OrganizationId == org && x.OrganizationUserId == intake.DeveloperId).Select(x => (Guid?)x.TeamId).SingleOrDefaultAsync(ct);
            intake.Status = request.Choice switch { "create" => "AwaitingProjectCreation", "existing" => "AwaitingAssignment", "manager" => "AwaitingManagerAssistance", _ => "Cancelled" };
            if (request.Choice == "existing")
            {
                var candidate = (await DiscoverAsync(org, installation, intake.Id, ct)).SingleOrDefault(x => x.Id == request.ProjectId)
                    ?? throw new UnauthorizedAccessException("Select an accessible project from the current project results.");
                var binding = await db.ProjectDeliveryBindings.SingleOrDefaultAsync(x => x.WorkstreamId == candidate.Id, ct);
                intake.ProjectId = candidate.Id; intake.BoardId = binding?.BoardId; intake.TeamId = candidate.TeamId; intake.ManagerId = candidate.ManagerId;
                try { await policy.RequireAsync(org, intake.DeveloperId, binding?.BoardId ?? Guid.Empty, ct); intake.Status = "Ready"; }
                catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException) { intake.Issue = e.Message; }
            }
        }
        intake.LastChoiceKey = request.IdempotencyKey; intake.LastChoiceMessageId = source.Id; intake.Revision++; intake.UpdatedAt = clock.GetUtcNow();
        setup.QueueIntake(intake); await db.SaveChangesAsync(ct); return Summary(intake);
    }
    private async Task CancelObsoleteAssistanceAsync(ProjectIntake intake, CancellationToken ct)
    {
        // The caller has validated a fresh choice from the requesting human. That choice also
        // withdraws only the coordination and hiring suggestion created for this intake.
        foreach (var session in await db.AgentCoordinationSessions.Where(x => x.OrganizationId == intake.OrganizationId && x.SourceIntakeId == intake.Id &&
            (x.Status == CSweet.Domain.Communications.AgentCoordinationStatus.Active || x.Status == CSweet.Domain.Communications.AgentCoordinationStatus.Summarizing)).ToListAsync(ct))
            await coordination.CancelAsync(intake.OrganizationId, intake.RequestingHumanId, true,
                new(session.Id, session.Revision, "The requesting human changed or cancelled project setup.", $"intake-choice:{intake.Id:N}:{intake.Revision}"), ct);
        if (intake.HiringRecommendationId is { } recommendation && intake.ChiefId is { } chief)
        {
            var installation = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == intake.OrganizationId && x.Id == chief).Select(x => x.AgentInstallationId).SingleOrDefaultAsync(ct);
            if (installation.HasValue)
                await hiring.WithdrawRecommendationAsync(intake.OrganizationId, installation.Value,
                    new(recommendation, "The requesting human changed or cancelled project setup.", $"intake-withdraw:{intake.Id:N}:{intake.Revision}"), ct);
        }
    }

    public static ProjectIntakeSummary Summary(ProjectIntake intake) => new(intake.Id, intake.Name, intake.Goal, intake.Status, intake.TicketOwner,
        intake.RequestingHumanId, intake.DeveloperId, intake.ProjectId, intake.BoardId, intake.RootItemId, intake.ManagerId, intake.TeamId, intake.ConversationId, intake.SourceMessageId, intake.Revision,
        intake.ProjectId.HasValue ? $"/organizations/{intake.OrganizationId:D}/projects/{intake.ProjectId:D}/members?intake={intake.Id:D}" : $"/organizations/{intake.OrganizationId:D}/projects/new?intake={intake.Id:D}", intake.Issue) { OriginalRequest = intake.OriginalRequest, SetupChoiceMessageId = intake.LastChoiceMessageId };
}
