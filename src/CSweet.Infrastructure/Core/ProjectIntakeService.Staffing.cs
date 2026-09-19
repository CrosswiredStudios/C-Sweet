using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectIntakeService
{
    public async Task<IReadOnlyList<ProjectIntakeSummary>> ListAssistanceAsync(Guid org, Guid installation, CancellationToken ct)
    {
        var actor = await ActorAsync(org, installation, ct);
        var chief = await IsCurrentChiefAsync(org, actor.Id, ct);
        var manager = await setup.HasRoleAsync(actor, "software-product-manager", ct);
        if (!chief && !manager) throw new UnauthorizedAccessException("Only the assigned Chief of Staff or project manager can discover assistance requests.");
        return (await db.ProjectIntakes.AsNoTracking().Where(x => x.OrganizationId == org && (chief && x.ChiefId == actor.Id || manager && x.ManagerId == actor.Id) && x.Status == "AwaitingManagerAssistance")
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(ct)).Select(Summary).ToArray();
    }
    public async Task<ProjectIntakeSummary> RequestManagerAsync(Guid org, Guid installation, ChooseProjectIntakeRequest request, CancellationToken ct)
    {
        if (request.Choice != "manager") throw new ArgumentException("Manager assistance requires the human's manager choice.");
        await ChooseAsync(org, installation, request, ct);
        var intake = await OwnedAsync(org, installation, request.IntakeId, ct);
        if (intake.Status != "AwaitingManagerAssistance" || intake.CoordinationSessionId.HasValue || intake.Issue is not null) return Summary(intake);
        var chiefs = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == org && x.IsActive && x.ArchivedAt == null && x.AgentInstallationId != null &&
            db.LeadershipAssignments.Any(a => a.OrganizationId == org && a.OrganizationUserId == x.Id && a.PositionKey == "chief-of-staff" && a.StartsAt <= clock.GetUtcNow() && (a.EndsAt == null || a.EndsAt > clock.GetUtcNow()))).ToListAsync(ct);
        if (chiefs.Count != 1)
        {
            intake.Issue = "No single active Chief of Staff is available. Create the project using the setup link, or ask an administrator to assign an available Chief of Staff.";
        }
        else
        {
            var chief = chiefs[0]; intake.ChiefId = chief.Id;
            if (!await HasCapabilitiesAsync(chief.AgentInstallationId!.Value, [ProjectIntakeCapabilities.Staffing, CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond], ct))
                intake.Issue = "The Chief of Staff needs project-assistance and coordination grants. Ask an administrator to update its grants, or use the manual project setup link.";
            else
            {
                await db.SaveChangesAsync(ct);
                var handoff = await StartIntakeCoordinationAsync(intake, intake.DeveloperId, installation, chief.Id, "project-manager-assistance.v1", ct);
                intake.CoordinationSessionId = handoff.Id;
            }
        }
        intake.Revision++; intake.UpdatedAt = clock.GetUtcNow(); setup.QueueIntake(intake); await db.SaveChangesAsync(ct);
        return Summary(intake);
    }
    private Task<bool> IsCurrentChiefAsync(Guid org, Guid employee, CancellationToken ct) =>
        db.LeadershipAssignments.AnyAsync(x => x.OrganizationId == org && x.OrganizationUserId == employee && x.PositionKey == "chief-of-staff" && x.StartsAt <= clock.GetUtcNow() && (x.EndsAt == null || x.EndsAt > clock.GetUtcNow()), ct);

    private async Task<bool> HasCapabilitiesAsync(Guid agent, string[] required, CancellationToken ct)
    {
        var json = await db.AgentInstallations.Where(x => x.Id == agent && x.IsEnabled && x.RevisionStatus == CSweet.Domain.Setup.PluginRevisionStatus.Active && x.SetupState == CSweet.Domain.Setup.PluginSetupState.Ready).Select(x => x.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(ct);
        var capabilities = JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? [];
        return required.All(x => capabilities.Contains(x));
    }
    private Task<AgentCoordinationSession> StartIntakeCoordinationAsync(ProjectIntake intake, Guid initiator, Guid installation, Guid target, string type, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(new ProjectManagerAssistanceRequest(intake.Id, intake.Name, intake.Goal, intake.DeveloperId, intake.RequestingHumanId, intake.TeamId) { OriginalRequest = intake.OriginalRequest }, Json);
        return coordination.StartAsync(intake.OrganizationId, initiator, installation, new(target,
            $"Project setup: {intake.Name}", intake.Goal,
            ["An authorized project exists", "The requesting developer is explicitly assigned", "The retained request can resume without duplicate tickets"],
            "The user requested project-manager assistance. Read the current intake before acting; this handoff does not authorize hiring or development.",
            intake.ConversationId, intake.SourceChatTurnId!.Value, intake.SourceMessageId,
            $"project-intake:{intake.Id:N}:{intake.LastChoiceMessageId:N}:{type}:{target:N}", new(type, "1", intake.Id.ToString("N"), 0, true, payload)) { SourceIntakeId = intake.Id }, ct);
    }
    public async Task<ProjectStaffingResult> StaffAsync(Guid org, Guid installation, ProjectStaffingRequest request, CancellationToken ct)
    {
        var actor = await ActorAsync(org, installation, ct);
        var intake = await db.ProjectIntakes.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == request.IntakeId && x.ChiefId == actor.Id, ct)
            ?? throw new UnauthorizedAccessException("This request was not handed to this Chief of Staff.");
        if (!await IsCurrentChiefAsync(org, actor.Id, ct) || intake.Status != "AwaitingManagerAssistance")
            throw new InvalidOperationException("This project request no longer needs staffing assistance.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200) throw new ArgumentException("A stable staffing key is required.");
        if (intake.ManagerId.HasValue && request.ManagerId.HasValue && request.ManagerId != intake.ManagerId)
            throw new InvalidOperationException("This intake already has a reserved manager. The human must change the setup choice before replacing that reservation.");
        var previous = (intake.ManagerId, intake.HiringRecommendationId, intake.Issue);
        var candidates = new List<ProjectManagerCandidate>();
        foreach (var user in await db.CoreOrganizationUsers.Where(x => x.OrganizationId == org && x.IsActive && x.ArchivedAt == null && x.AgentInstallationId != null).ToListAsync(ct))
        {
            if (intake.ManagerId.HasValue && user.Id != intake.ManagerId) continue;
            if (!await setup.HasRoleAsync(user, "software-product-manager", ct) ||
                !await HasCapabilitiesAsync(user.AgentInstallationId!.Value, [ProjectIntakeCapabilities.ManagerSetup, CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond], ct)) continue;
            var team = await db.TeamMemberships.Where(x => x.OrganizationUserId == user.Id).Select(x => (Guid?)x.TeamId).SingleOrDefaultAsync(ct);
            if (team.HasValue && intake.TeamId.HasValue && team != intake.TeamId) continue;
            if (await db.Workstreams.AnyAsync(x => x.OrganizationId == org && x.AccountableManagerOrganizationUserId == user.Id && x.Status != WorkstreamStatus.Completed && x.Status != WorkstreamStatus.Cancelled, ct)) continue;
            if (await db.ProjectManagerReservations.AnyAsync(x => x.OrganizationUserId == user.Id && x.IntakeId != intake.Id, ct)) continue;
            candidates.Add(new(user.Id, user.DisplayName, team));
        }
        if (request.ManagerId is { } manager)
        {
            if (!candidates.Any(x => x.Id == manager)) throw new InvalidOperationException("The selected manager is busy or belongs to an incompatible team.");
            var reservation = await db.ProjectManagerReservations.SingleOrDefaultAsync(x => x.OrganizationUserId == manager, ct);
            if (reservation is null) db.ProjectManagerReservations.Add(new() { OrganizationId = org, OrganizationUserId = manager, IntakeId = intake.Id });
            intake.ManagerId = manager; intake.Issue = null;
            await db.SaveChangesAsync(ct);
            await StartIntakeCoordinationAsync(intake, actor.Id, installation, manager, "project-manager-setup.v1", ct);
        }
        else if (candidates.Count == 0 && intake.ManagerId.HasValue)
            intake.Issue = "The reserved project manager is unavailable or no longer has the required access. Restore that manager's access, or change the setup choice and use the manual project form.";
        else if (candidates.Count == 0 && intake.HiringRecommendationId is null)
        {
            var recommendation = await hiring.UpsertRecommendationAsync(org, installation, new CSweet.Contracts.Core.UpsertHiringRecommendationRequest(
                "Project manager for " + intake.Name, intake.Goal.Length <= 2048 ? intake.Goal : intake.Goal[..2048], null, [], null,
                $"project-manager:{intake.Id:N}:{intake.LastChoiceMessageId:N}") { RoleKey = "software-product-manager", TeamId = intake.TeamId }, ct);
            intake.HiringRecommendationId = recommendation.Id;
            intake.Issue = "A project-manager hiring recommendation is awaiting human review. Development will resume only after project setup and assignment are complete.";
        }
        if (previous != (intake.ManagerId, intake.HiringRecommendationId, intake.Issue))
        { intake.Revision++; intake.UpdatedAt = clock.GetUtcNow(); setup.QueueIntake(intake); await db.SaveChangesAsync(ct); }
        return new(Summary(intake), candidates, intake.HiringRecommendationId);
    }
    public async Task<ProjectIntakeSummary> ManagerSetupAsync(Guid org, Guid installation, Guid id, CancellationToken ct)
    {
        var actor = await ActorAsync(org, installation, ct);
        var intake = await db.ProjectIntakes.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == id && x.ManagerId == actor.Id && x.Status == "AwaitingManagerAssistance", ct)
            ?? throw new UnauthorizedAccessException("This setup request is not assigned to this manager.");
        return Summary(intake);
    }
}
