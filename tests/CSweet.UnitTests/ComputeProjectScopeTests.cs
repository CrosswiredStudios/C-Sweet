using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeProjectScopeTests
{
    private static ComputeDefaultsService Service(ComputeBrokerTests.Fixture f) => new(f.Db, TimeProvider.System,
        new(f.Db, TimeProvider.System, new CSweet.Infrastructure.Setup.AuditExecutionContextAccessor()));

    private static async Task<OrganizationUser> PrepareProjectAsync(ComputeBrokerTests.Fixture f)
    {
        var person = await f.Db.CoreOrganizationUsers.SingleAsync();
        var project = await f.Db.Workstreams.SingleAsync();
        project.Status = WorkstreamStatus.Approved;
        // Match Victor's existing game project: a team and board, no ProjectDeliveryBinding.
        project.AccountableManagerOrganizationUserId = Guid.NewGuid();
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = f.Organization };
        f.Db.Add(team);
        f.Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            TeamId = team.Id, OrganizationUserId = person.Id });
        f.Db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            WorkstreamId = project.Id, TeamId = team.Id });
        f.Db.WorkBoards.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            WorkstreamId = project.Id, TeamId = team.Id });
        await f.Db.SaveChangesAsync();
        return person;
    }

    private static async Task<ComputeLocalSetup> ReadyAsync(ComputeBrokerTests.Fixture f, ComputeDefaultsService service)
    {
        await service.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync();
        setup.State = "Ready"; setup.TemplateId = "project-test";
        f.Db.ComputeTemplates.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            TemplateId = setup.TemplateId, Enabled = true,
            TemplateJson = JsonSerializer.Serialize(new ComputeTemplate(setup.TemplateId, "linux", "x64",
                "sha256:" + new string('a', 64), ["docker", "docker-apps-v1"]), ComputeProtocol.Json) });
        await f.Db.SaveChangesAsync();
        return setup;
    }

    [Fact]
    public async Task Team_member_uses_existing_project_for_defaults_grants_and_compute_admission_without_manifest_opt_in()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        await PrepareProjectAsync(f);
        var service = Service(f); var setup = await ReadyAsync(f, service);
        var defaults = await service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default);
        Assert.Equal("Ready", defaults.State); Assert.Equal(f.Workstream, defaults.WorkstreamId);
        Assert.Contains("csweet/python:3.12", defaults.CachedImages);
        Assert.Equal(defaults.WorkstreamId, (await service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default)).WorkstreamId);
        Assert.Equal(f.Workstream, (await service.ReadAsync(f.Organization, f.Installation, default)).WorkstreamId);
        var grants = await f.Db.ScopedActionGrants.Where(x => x.ConstraintsJson.Contains("project-test")).ToListAsync();
        Assert.Equal(7, grants.Count);
        Assert.All(grants, grant => Assert.Equal(f.Workstream, grant.ScopeId));
        Assert.DoesNotContain(grants, grant => grant.Action.StartsWith("network."));
        var template = JsonSerializer.Deserialize<ComputeTemplate>((await f.Db.ComputeTemplates.SingleAsync()).TemplateJson, ComputeProtocol.Json)!;
        var broker = new ComputeBroker(f.Db, new ProjectCatalog(template), TimeProvider.System, f.Ledger);
        var request = new RequestComputeEnvironment(f.Workstream, "project-test", "project-test",
            new("linux", "x64", template.Id, new(2, 2048, 20480), 0));
        var first = await broker.RequestAsync(f.Organization, f.Installation, request, default);
        Assert.Equal(first.Id, (await broker.RequestAsync(f.Organization, f.Installation, request, default)).Id);
        Assert.Equal(f.Workstream, (await f.Db.ComputeEnvironments.SingleAsync()).WorkstreamId);
        Assert.Single(await f.Db.Workstreams.ToListAsync()); Assert.Empty(await f.Db.Set<ComputeAgentAccess>().ToListAsync());
        await service.ActivateAccessAsync(setup, default); await service.ActivateAccessAsync(setup, default);
        Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == "com.csweet.compute.available.v1").ToListAsync());
    }

    [Fact]
    public async Task Project_grant_revocation_is_preserved_and_blocks_admission()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await PrepareProjectAsync(f);
        var service = Service(f); await ReadyAsync(f, service);
        await service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default);
        var grant = await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Provision && x.ConstraintsJson.Contains("project-test"));
        grant.RevokedAt = DateTimeOffset.UtcNow; grant.Revision++; await f.Db.SaveChangesAsync();
        await service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default);
        Assert.NotNull(grant.RevokedAt); Assert.Equal(2, grant.Revision);
        var template = JsonSerializer.Deserialize<ComputeTemplate>((await f.Db.ComputeTemplates.SingleAsync()).TemplateJson, ComputeProtocol.Json)!;
        var broker = new ComputeBroker(f.Db, new ProjectCatalog(template), TimeProvider.System, f.Ledger);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.RequestAsync(f.Organization, f.Installation,
            new(f.Workstream, "test", "test", new("linux", "x64", template.Id, new(2, 2048, 20480), 0)), default));
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
    }

    [Theory]
    [InlineData("removed-team")]
    [InlineData("archived-team")]
    [InlineData("cancelled-project")]
    [InlineData("archived-agent")]
    [InlineData("other-organization")]
    public async Task Project_defaults_do_not_grant_access_outside_current_authority(string change)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var actor = await PrepareProjectAsync(f); var service = Service(f); await ReadyAsync(f, service);
        switch (change)
        {
            case "removed-team": (await f.Db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow; break;
            case "archived-team": (await f.Db.OrganizationTeams.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
            case "cancelled-project": (await f.Db.Workstreams.SingleAsync()).Status = WorkstreamStatus.Cancelled; break;
            case "archived-agent": actor.ArchivedAt = DateTimeOffset.UtcNow; break;
        }
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadProjectAsync(
            change == "other-organization" ? Guid.NewGuid() : f.Organization, f.Installation, f.Workstream, default));
        Assert.DoesNotContain(await f.Db.ScopedActionGrants.ToListAsync(), grant => grant.ConstraintsJson.Contains("project-test"));
    }

    [Fact]
    public async Task Project_required_agent_uses_canonical_board_without_legacy_delivery_binding()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var person = await PrepareProjectAsync(f);
        var installation = await f.Db.AgentInstallations.Include(x => x.PackageVersion).SingleAsync();
        installation.PackageVersion!.ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}";
        var board = await f.Db.WorkBoards.SingleAsync();
        board.Kind = CSweet.Domain.WorkManagement.WorkBoardKind.Standard;
        f.Db.ProjectParticipants.Add(new() { OrganizationId = f.Organization,
            WorkstreamId = f.Workstream, OrganizationUserId = person.Id });
        var grant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = f.Installation,
            ScopeKind = GrantScopeKind.Board, ScopeId = board.Id, Action = CSweet.Contracts.WorkManagement.WorkItemActions.Read };
        f.Db.ScopedActionGrants.Add(grant);
        await f.Db.SaveChangesAsync();
        var service = Service(f); await ReadyAsync(f, service);
        Assert.Empty(await f.Db.ProjectDeliveryBindings.ToListAsync());
        Assert.Equal("Ready", (await service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default)).State);
        grant.RevokedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default));
        grant.RevokedAt = null; board.ArchivedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default));
        board.ArchivedAt = null; (await f.Db.ProjectParticipants.SingleAsync()).RemovedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default));
    }

    [Fact]
    public async Task Project_required_agents_retain_explicit_participant_and_board_checks()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await PrepareProjectAsync(f);
        var installation = await f.Db.AgentInstallations.Include(x => x.PackageVersion).SingleAsync();
        installation.PackageVersion!.ManifestJson = "{\"rolePolicy\":{\"requiresProject\":true}}";
        await f.Db.SaveChangesAsync(); var service = Service(f); await ReadyAsync(f, service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadProjectAsync(f.Organization, f.Installation, f.Workstream, default));
        Assert.Empty(await f.Db.Set<ComputeAgentAccess>().ToListAsync()); Assert.Single(await f.Db.Workstreams.ToListAsync());
    }

    [Fact]
    public async Task Unscoped_defaults_never_guess_between_projects_or_fall_back_to_legacy_scope()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await PrepareProjectAsync(f);
        var person = await f.Db.CoreOrganizationUsers.SingleAsync();
        f.Db.Workstreams.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = person.Id });
        await f.Db.SaveChangesAsync(); var service = Service(f); var setup = await ReadyAsync(f, service);
        f.Db.Add(new ComputeAgentAccess { Id = f.Installation, OrganizationId = f.Organization, SetupId = setup.Id,
            WorkstreamId = Guid.NewGuid() }); await f.Db.SaveChangesAsync();
        var defaults = await service.ReadAsync(f.Organization, f.Installation, default);
        Assert.Equal("project_selection_required", defaults.ErrorCode);
        Assert.Null(defaults.WorkstreamId); Assert.Null(defaults.TemplateId);
        Assert.DoesNotContain(await f.Db.ScopedActionGrants.ToListAsync(), grant => grant.ConstraintsJson.Contains("project-test"));
    }

    [Fact]
    public async Task Failed_host_setup_requires_explicit_retry_even_without_personal_access()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var service = Service(f);
        await service.EnsureRequestedAsync(f.Installation, default);
        var setup = await f.Db.Set<ComputeLocalSetup>().SingleAsync();
        setup.State = "Failed"; setup.ErrorCode = "setup_interrupted"; await f.Db.SaveChangesAsync();
        await service.EnsureRequestedAsync(f.Installation, default); Assert.Equal("Failed", setup.State);
        await service.EnsureRequestedAsync(f.Installation, default, retryFailed: true);
        Assert.Equal("Pending", setup.State); Assert.Null(setup.ErrorCode); Assert.Equal(2, setup.Revision);
        Assert.Empty(await f.Db.Set<ComputeAgentAccess>().ToListAsync()); Assert.Single(await f.Db.Workstreams.ToListAsync());
    }

    private sealed class ProjectCatalog(ComputeTemplate template) : IComputeTemplateCatalog
    {
        public Task<RegisteredComputeTemplate?> ResolveAsync(Guid org, string id, CancellationToken token) =>
            Task.FromResult<RegisteredComputeTemplate?>(new(template, "test-provider", Guid.Parse("11111111-1111-1111-1111-111111111111")));
    }
}
