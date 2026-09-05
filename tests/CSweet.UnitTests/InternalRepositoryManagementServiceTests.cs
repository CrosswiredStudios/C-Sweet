using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class InternalRepositoryManagementServiceTests
{
    [Fact]
    public async Task ManagerCanCreateInspectRenameArchiveAndRestoreWithAudit()
    {
        await using var db = Database();
        var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Manager);
        await db.SaveChangesAsync();
        var host = new Host(); var audit = new Audit();
        var service = new InternalRepositoryManagementService(db, host, audit, TimeProvider.System);
        var created = await service.CreateAsync(business, user, new("engine", "trunk"), default);
        Assert.Equal("Ready", created.Status);
        Assert.Single(db.SourceControlConnections);
        var details = await service.InspectAsync(business, user, created.Id, null, null, default);
        var changed = await service.UpdateAsync(business, user, created.Id, new("game-engine", "trunk", true, details.Revision), default);
        Assert.Equal("Archived", changed.Status);
        Assert.Single(await service.ListAsync(business, user, default));
        details = await service.InspectAsync(business, user, created.Id, null, null, default);
        var restored = await service.UpdateAsync(business, user, created.Id, new("game-engine", "trunk", false, details.Revision), default);
        Assert.Equal("Ready", restored.Status);
        Assert.Equal(6, audit.Events.Count);
        Assert.All(audit.Events, e => Assert.Equal(user, e.Actor!.ApplicationUserId));
        Assert.All(audit.Events, e => Assert.Equal(business, e.OrganizationId));
    }

    [Fact]
    public async Task DeleteRequiresArchiveExactNameAndCurrentRevision()
    {
        await using var db = Database();
        var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var created = await service.CreateAsync(business, user, new("engine"), default);
        var details = await service.InspectAsync(business, user, created.Id, null, null, default);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(business, user, created.Id, new("engine", details.Revision), default));
        await service.UpdateAsync(business, user, created.Id, new("engine", "main", true, details.Revision), default);
        details = await service.InspectAsync(business, user, created.Id, null, null, default);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(business, user, created.Id, new("wrong", details.Revision), default));
        Assert.True(await service.DeleteAsync(business, user, created.Id, new("engine", details.Revision), default));
        Assert.Empty(db.SourceControlRepositories);
    }

    [Fact]
    public async Task ViewerAndNonmemberCannotMutateOrInspectAnotherBusiness()
    {
        await using var db = Database();
        var business = Guid.NewGuid(); var viewer = Guid.NewGuid();
        Seed(db, business, viewer, OrganizationPermissionLevel.Viewer);
        await db.SaveChangesAsync();
        var host = new Host();
        var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(business, viewer, new("no"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(Guid.NewGuid(), viewer, default));
        Assert.Empty(db.SourceControlRepositories);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task StaleRevisionCannotChangeRepository()
    {
        await using var db = Database();
        var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var host = new Host();
        var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        var created = await service.CreateAsync(business, user, new("engine"), default);
        var calls = host.Calls;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.UpdateAsync(business, user, created.Id, new("other", "other", false, -1), default));
        Assert.Equal(calls, host.Calls);
        Assert.Equal("engine", (await service.ListAsync(business, user, default))[0].Name);
    }

    [Fact]
    public async Task FailedProvisioningCanRetryWithoutCreatingDuplicateRepository()
    {
        await using var db = Database();
        var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var host = new Host { Fail = true };
        var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(business, user, new("engine"), default));
        host.Fail = false;
        Assert.Equal("Ready", (await service.CreateAsync(business, user, new("engine"), default)).Status);
        Assert.Single(db.SourceControlRepositories);
    }

    [Fact]
    public async Task TeamAccessSupportsPrimaryReassignmentRevocationAndRejectsStaleEdits()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); var team = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner);
        db.OrganizationTeams.Add(new() { Id = team, OrganizationId = business, Name = "Development" });
        await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var first = await service.CreateAsync(business, user, new("engine"), default);
        var second = await service.CreateAsync(business, user, new("tools"), default);
        await service.SetTeamAsync(business, user, first.Id, new(team, true, "LeadAuthorizedAutoMerge"), default);
        await service.SetTeamAsync(business, user, second.Id, new(team, true, "LeadAndAdministratorApproval"), default);
        Assert.False((await service.TeamAccessAsync(business, user, first.Id, default)).Single().IsPrimary);
        var access = (await service.TeamAccessAsync(business, user, second.Id, default)).Single();
        Assert.True(access.IsPrimary);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SetTeamAsync(business, user, second.Id, new(team, true, "LeadAuthorizedAutoMerge", access.Revision - 1), default));
        await service.SetTeamAsync(business, user, second.Id, new(team, false, access.MergeApprovalMode, access.Revision, true), default);
        Assert.True((await service.TeamAccessAsync(business, user, second.Id, default)).Single().Disabled);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetTeamAsync(business, user, second.Id, new(Guid.NewGuid(), true, "LeadAuthorizedAutoMerge"), default));
    }

    [Fact]
    public async Task ProposalInspectionUsesPersistedShaAndRejectsAnotherRepository()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); await db.SaveChangesAsync();
        var host = new Host(); var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        var repository = await service.CreateAsync(business, user, new("engine"), default);
        var other = await service.CreateAsync(business, user, new("tools"), default);
        var proposal = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, CommitSha = new string('a', 40), TargetBranch = "main", TicketBranch = "feature" };
        db.SourceControlPublications.Add(proposal);
        db.SourceControlValidations.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, PublicationId = proposal.Id, CommitSha = new string('b', 40), Status = SourceControlValidationStatus.Passed });
        await db.SaveChangesAsync();
        Assert.False((await service.ProposalsAsync(business, user, repository.Id, default)).Single().QaPassed);
        await service.ProposalDiffAsync(business, user, repository.Id, proposal.Id, default);
        Assert.Equal(proposal.CommitSha, host.Last!.ExpectedSha); Assert.Equal("compare", host.Last.Operation);
        var calls = host.Calls;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ProposalDiffAsync(business, user, other.Id, proposal.Id, default));
        Assert.Equal(calls, host.Calls);
    }

    [Fact]
    public async Task CreationPolicyPersistsOptOutAndRejectsStaleOrCrossBusinessSettings()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var initial = await service.ProvisioningSettingsAsync(business, user, default);
        Assert.True(initial.Enabled); Assert.False(initial.RequiresApproval); Assert.Empty(db.SourceControlRepositories);
        var update = new UpdateInternalGitProvisioningSettings(false, true, 10, null, "project", "trunk", initial.Revision);
        var changed = await service.UpdateProvisioningSettingsAsync(business, user, update, default);
        Assert.False(changed.Enabled); Assert.Equal("trunk", changed.DefaultBranch);
        await InternalGitProvisioningDefaults.EnsureAsync(db, business, default);
        Assert.False((await service.ProvisioningSettingsAsync(business, user, default)).Enabled);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.UpdateProvisioningSettingsAsync(business, user, update, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateProvisioningSettingsAsync(business, user, update with { ExpectedRevision = changed.Revision, DefaultTeamId = Guid.NewGuid() }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ProvisioningSettingsAsync(Guid.NewGuid(), user, default));
        Assert.Single(db.RepositoryProvisioningPolicies); Assert.Single(db.SourceControlRepositoryTemplates);
    }

    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static void Seed(CSweetDbContext db, Guid business, Guid user, OrganizationPermissionLevel permission) =>
        db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ApplicationUserId = user,
            DisplayName = "Operator", PermissionLevel = permission, EmployeeType = EmployeeType.Human, IsActive = true });
    private sealed class Audit : IAuditEventWriter
    {
        public List<AuditEventWriteRequest> Events { get; } = [];
        public Task WriteAsync(string type, string entity, Guid? id, string? summary, string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default)
        { Events.Add(request); return Task.FromResult(Guid.NewGuid()); }
    }
    private sealed class Host : ITrustedSourceControlHostClient
    {
        public InternalGitRepositoryRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<InternalGitRepositoryInspection> ExecuteInternalAsync(InternalGitRepositoryRequest request, CancellationToken cancellationToken = default)
        {
            Last = request; Calls++;
            if (Fail) throw new IOException("Storage unavailable.");
            return Task.FromResult(new InternalGitRepositoryInspection(request.Name ?? "main", [], [], []));
        }
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
