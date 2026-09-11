using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;
namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_finding_ticket_requires_board_permission_and_copies_evidence_once(bool permit)
    {
        await using var db = CreateDb(); var setup = SeedInstallation(db);
        var installation = db.AgentInstallations.Local.Single();
        installation.Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = setup.InstallationId,
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { WebPreviewTriageCapabilities.ReadFinding, WebPreviewTriageCapabilities.CreateTicket, WorkItemActions.Create }) };
        var actor = db.CoreOrganizationUsers.Local.Single();
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = "Demo", AccountableManagerOrganizationUserId = actor.Id };
        db.Workstreams.Add(project);
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, WorkstreamId = project.Id,
            Name = "Defects", Kind = WorkBoardKind.Standard, Columns = [Column("To do", WorkBoardColumnCategory.ToDo, 0)] };
        db.WorkBoards.Add(board);
        if (permit) Grant(db, setup, WorkItemActions.Create, GrantScopeKind.Board, board.Id);
        var job = new WebPreviewJobRecord { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, WorkstreamId = project.Id,
            InstallationId = setup.InstallationId, Phase = "Stopped", TeardownConfirmedAt = DateTimeOffset.UtcNow, BuildId = Guid.NewGuid() };
        db.WebPreviewJobs.Add(job);
        var evidence = new PreviewDiagnostic(Guid.NewGuid(), job.Id, project.Id, job.BuildId, new string('a',40), "app", "runtime",
            "Crash", "Observed division by zero while loading a level.", DateTimeOffset.UtcNow, false);
        var finding = new WebPreviewFindingRecord { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, ProjectId = project.Id,
            PreviewId = job.Id, BuildId = job.BuildId, Fingerprint = "sha256:" + new string('d',64),
            EvidenceJson = JsonSerializer.Serialize(evidence, PreviewJson.Options), RetainUntil = DateTimeOffset.UtcNow.AddDays(7), BoardId = board.Id };
        var parent = new WorkTask { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            BoardColumnId = board.Columns.Single().Id, Title = "Existing product story", TypeKey = Wire.WorkItemTypeKeys.GeneralStoryV1 };
        db.CoreWorkTasks.Add(parent);
        db.WebPreviewTriageRoutes.Add(new() { ProjectId = project.Id, OrganizationId = setup.OrganizationId, InstallationId = setup.InstallationId,
            BoardId = board.Id, ParentItemId = parent.Id });
        db.WebPreviewFindings.Add(finding); await db.SaveChangesAsync();
        var triage = new WebPreviewTriageService(db, new(db, TimeProvider.System), TimeProvider.System);
        var handler = new WebPreviewTriageCapabilityHandler(db, triage, CreateHandler(db, new TestAuditEventWriter()), TimeProvider.System);
        var ticket = new Wire.CreateWorkItemRequest(board.Id, "Level loading crash", "Investigate the observed failure.", "Task", "Medium", null, parent.Id, null, "ignored")
            { TypeKey = Wire.WorkItemTypeKeys.GeneralTaskV1, Planning = new(["Fix level loading"], ["The preview loads the same level without an exception."]) };
        async Task<CapabilityResult> Invoke() => await handler.HandleAsync(Session(setup, WebPreviewTriageCapabilities.CreateTicket, WorkItemActions.Create),
            new RequestCapability { RequestId = Guid.NewGuid().ToString("N"), Capability = WebPreviewTriageCapabilities.CreateTicket,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PreviewFindingTicketRequest(finding.Id, JsonSerializer.SerializeToElement(ticket, PreviewJson.Options)), PreviewJson.Options)) }, default).SingleAsync();
        var first = await Invoke(); Assert.True(first.Succeeded == permit, first.Error);
        if (!permit) { Assert.Empty(await db.CoreWorkTasks.Where(x => x.Id != parent.Id).ToListAsync()); return; }
        Assert.True((await Invoke()).Succeeded);
        var saved = Assert.Single(await db.CoreWorkTasks.Where(x => x.Id != parent.Id).ToListAsync());
        Assert.Contains(evidence.Summary, saved.Description); Assert.Contains(evidence.SourceRevision, saved.Description);
        Assert.Contains(job.BuildId.ToString("D"), saved.Description); Assert.Equal(saved.Id, (await db.WebPreviewFindings.SingleAsync()).TicketId);
        db.WebPreviewEvidence.RemoveRange(await db.WebPreviewEvidence.ToListAsync()); await db.SaveChangesAsync();
        Assert.Contains(evidence.Summary, (await db.CoreWorkTasks.SingleAsync(x => x.Id != parent.Id)).Description);
    }
}
