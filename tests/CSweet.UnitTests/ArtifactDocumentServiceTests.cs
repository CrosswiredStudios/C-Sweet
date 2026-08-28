using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ArtifactDocumentServiceTests
{
    [Fact]
    public async Task Accepted_revision_remains_authoritative_while_a_new_draft_exists()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);

        var created = await service.CreateAsync(organization.Id, actor,
            new("Vision", "# One", GameDesignDocumentTypes.HighLevelGdd, "create-one"));
        var submitted = await service.SubmitAsync(organization.Id, actor, created.Document.Id,
            new(created.LatestRevision.Id, "submit-one"));
        var accepted = await service.DecideAsync(organization.Id, actor, created.Document.Id,
            new(submitted.LatestRevision.Id, "accept", null, "accept-one"));
        var draft = await service.ReviseAsync(organization.Id, actor, created.Document.Id,
            new(accepted.LatestRevision.Id, "# Two", "revise-two"));
        var detail = await service.GetAsync(organization.Id, actor, created.Document.Id);

        Assert.NotNull(detail);
        Assert.Equal(draft.Id, detail!.Document.LatestRevisionId);
        Assert.Equal(accepted.LatestRevision.Id, detail.Document.AcceptedRevisionId);
        Assert.Equal("# One", detail.AcceptedRevision!.Content);
        Assert.Equal("# Two", detail.LatestRevision.Content);
    }

    [Fact]
    public async Task Ordinary_employee_cannot_discover_or_read_an_unshared_document()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        var employee = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Employee", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(employee);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = new ArtifactDocumentService(db, audit, TimeProvider.System);
        var document = await service.CreateAsync(organization.Id,
            new(owner.ApplicationUserId!.Value), new("Private", "secret", "test.v1", "private-create"));

        var results = await service.BrowseAsync(organization.Id,
            new(employee.ApplicationUserId!.Value), new());
        var read = await service.GetAsync(organization.Id,
            new(employee.ApplicationUserId.Value), document.Document.Id);

        Assert.Empty(results);
        Assert.Null(read);
        Assert.Contains(audit.Events, x => x.Category == "DocumentAccess" && x.Outcome == "Denied");
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Organization, OrganizationUser) SeedHuman(CSweetDbContext db, OrganizationPermissionLevel level)
    {
        var now = DateTimeOffset.UtcNow;
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Studio", CreatedAt = now, UpdatedAt = now };
        var user = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Owner", EmployeeType = EmployeeType.Human, PermissionLevel = level, CreatedAt = now
        };
        db.CoreOrganizations.Add(organization);
        db.CoreOrganizationUsers.Add(user);
        return (organization, user);
    }
}
