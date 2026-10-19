using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    public async Task<RenameProjectResponse> RenameAsync(OrganizationUser actor, Guid id, RenameProjectRequest request, CancellationToken ct)
    {
        var project = await RequireManagerAsync(actor, id, ct);
        if (!actor.IsActive || actor.ArchivedAt is not null) throw new UnauthorizedAccessException("An active human manager is required.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 512)
            throw new ArgumentException("Enter a project name of up to 512 characters.");
        var name = request.Name.Trim();
        // A lost-response retry has no further effects, including after another edit.
        if (project.Name == name) return new(id, project.Name, project.Revision);
        if (project.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The project changed. Refresh before saving its name.");
        var previousName = project.Name;
        project.Name = name;
        project.Revision++;
        project.UpdatedAt = clock.GetUtcNow();
        await QueueNameChangeAsync(project, previousName, actor, ct);
        var now = clock.GetUtcNow();
        var context = new W.AgentWorkContext(project.OrganizationId, project.Id, null, null, null, null, null, project.Id, null, project.ProfileKey);
        var changed = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "Workstream", project.Id,
            project.Revision, project.ProfileKey ?? "workstream", "renamed",
            JsonSerializer.SerializeToElement(new { previousName, name, actorId = actor.Id }));
        db.AgentPlatformEventOutbox.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, EventType = W.WorkstreamEventNames.ChangedV2,
            DataJson = JsonSerializer.Serialize(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            IdempotencyKey = $"{W.WorkstreamEventNames.ChangedV2}:{project.Id:N}:{project.Revision}", NextAttemptAt = now, OccurredAt = now
        });
        await db.SaveChangesAsync(ct);
        return new(id, project.Name, project.Revision);
    }

    // Both the human editor and approval-bound agent changes use the same continuity effects.
    internal async Task QueueNameChangeAsync(Workstream project, string previousName, OrganizationUser actor, CancellationToken ct)
    {
        if (project.Name == previousName) return;
        // Preserve custom board titles, keys, repository names and all identity/authority bindings.
        // Board labels have a smaller database limit than project names.
        var previousBoardName = previousName.Length > 160 ? previousName[..160] : previousName;
        var boardName = project.Name.Length > 160 ? project.Name[..160] : project.Name;
        foreach (var board in await db.WorkBoards.Where(x => x.OrganizationId == project.OrganizationId &&
            x.WorkstreamId == project.Id && x.Name == previousBoardName && x.ArchivedAt == null).ToListAsync(ct))
        {
            board.Name = boardName;
            board.Revision++;
            board.UpdatedAt = clock.GetUtcNow();
        }
        db.QueueAudit(new AuditEventWriteRequest("workstream.renamed.v1", "Workstream", OrganizationId: project.OrganizationId,
            EntityType: "Workstream", EntityId: project.Id, Summary: $"Renamed project from '{previousName}' to '{project.Name}'.",
            MetadataJson: JsonSerializer.Serialize(new { projectId = project.Id, previousName, name = project.Name, project.Revision }),
            OccurredAt: clock.GetUtcNow(), Actor: new AuditActor(actor.EmployeeType.ToString(), ApplicationUserId: actor.ApplicationUserId,
                OrganizationUserId: actor.Id, DisplayName: actor.DisplayName, InstallationId: actor.AgentInstallationId)));
        QueueProject(project);
    }
}
