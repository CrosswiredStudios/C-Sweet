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
    public async Task RepositoryRetryAndEditsPersistWhenQueriesDoNotTrackByDefault()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var host = new Host { Fail = true }; var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(business, user, new("engine"), default));
        db.ChangeTracker.Clear(); host.Fail = false;
        var created = await service.CreateAsync(business, user, new("engine"), default);
        db.ChangeTracker.Clear(); Assert.Equal(SourceControlRepositoryStatus.Ready, (await db.SourceControlRepositories.SingleAsync()).Status);
        var details = await service.InspectAsync(business, user, created.Id, null, null, default);
        await service.UpdateAsync(business, user, created.Id, new("renamed", "main", true, details.Revision), default);
        db.ChangeTracker.Clear(); var archived = await db.SourceControlRepositories.SingleAsync();
        Assert.Equal("renamed", archived.Name); Assert.NotNull(archived.ArchivedAt); Assert.Equal(SourceControlRepositoryStatus.Archived, archived.Status);
        await service.UpdateAsync(business, user, created.Id, new("renamed", "main", false, archived.Revision), default);
        db.ChangeTracker.Clear(); Assert.Null((await db.SourceControlRepositories.SingleAsync()).ArchivedAt);
    }

    [Fact]
    public async Task PrimaryTeamReassignmentAndRevocationPersistWithoutImplicitTracking()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); var team = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); db.OrganizationTeams.Add(new() { Id = team, OrganizationId = business, Name = "Development" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var first = await service.CreateAsync(business, user, new("engine"), default);
        var second = await service.CreateAsync(business, user, new("tools"), default);
        await service.SetTeamAsync(business, user, first.Id, new(team, true, "LeadAuthorizedAutoMerge"), default); db.ChangeTracker.Clear();
        await service.SetTeamAsync(business, user, second.Id, new(team, true, "LeadAndAdministratorApproval"), default); db.ChangeTracker.Clear();
        Assert.False((await service.TeamAccessAsync(business, user, first.Id, default)).Single().IsPrimary);
        var access = (await service.TeamAccessAsync(business, user, second.Id, default)).Single();
        await service.SetTeamAsync(business, user, second.Id, new(team, false, access.MergeApprovalMode, access.Revision, true), default); db.ChangeTracker.Clear();
        var revoked = (await service.TeamAccessAsync(business, user, second.Id, default)).Single(); Assert.True(revoked.Disabled); Assert.False(revoked.IsPrimary);
    }

    [Fact]
    public async Task ProvisioningPolicyAndTemplateChangesPersistWithoutImplicitTracking()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var settings = await service.ProvisioningSettingsAsync(business, user, default); db.ChangeTracker.Clear();
        await service.UpdateProvisioningSettingsAsync(business, user, new(false, true, 7, null, "project", "trunk", settings.Revision), default);
        db.ChangeTracker.Clear(); var saved = await service.ProvisioningSettingsAsync(business, user, default);
        Assert.False(saved.Enabled); Assert.True(saved.RequiresApproval); Assert.Equal(7, saved.MaximumRepositories);
        Assert.Equal("project", saved.NamePrefix); Assert.Equal("trunk", saved.DefaultBranch); Assert.Equal(settings.Revision + 1, saved.Revision);
    }

    [Fact]
    public async Task DisconnectUnusedGitHubChecksConfirmationRevisionAndRevokesCredentials()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); var viewer = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Manager); Seed(db, business, viewer, OrganizationPermissionLevel.Viewer);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Name = "Unused GitHub", Provider = SourceControlProvider.GitHub,
            Status = SourceControlConnectionStatus.Connected, SourceAccessInstallationId = 12 };
        var credential = new SourceControlCredential { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, ProtectedPayload = "secret" };
        db.AddRange(connection, credential); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var audit = new Audit(); var host = new Host(); var service = new InternalRepositoryManagementService(db, host, audit, TimeProvider.System);
        Assert.True((await service.ConnectionDisconnectPlanAsync(business, user, connection.Id, default)).CanDisconnect);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DisconnectConnectionAsync(business, viewer, connection.Id, new(connection.Name, 1), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConnectionDisconnectPlanAsync(Guid.NewGuid(), user, connection.Id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DisconnectConnectionAsync(business, user, connection.Id, new("Wrong", 1), default));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.DisconnectConnectionAsync(business, user, connection.Id, new(connection.Name, 0), default));
        var result = await service.DisconnectConnectionAsync(business, user, connection.Id, new(connection.Name, 1), default);
        Assert.Equal("Disconnected", result.Status); Assert.Equal(2, result.Revision);
        db.ChangeTracker.Clear(); var persisted = await db.SourceControlConnections.SingleAsync(); Assert.NotNull(persisted.DisconnectedAt); Assert.Equal(12, persisted.SourceAccessInstallationId);
        Assert.NotNull((await db.SourceControlCredentials.SingleAsync()).RevokedAt); Assert.Equal(0, host.Calls);
        Assert.False((await service.ConnectionDisconnectPlanAsync(business, user, connection.Id, default)).CanDisconnect);
        Assert.Equal(new[] { "Started", "Completed" }, audit.Events.Select(e => e.Outcome));
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("repository")]
    [InlineData("archived")]
    [InlineData("template")]
    [InlineData("policy")]
    [InlineData("request")]
    [InlineData("setup")]
    public async Task DisconnectRechecksDependenciesAndPreservesHistory(string dependency)
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); Seed(db, business, user, OrganizationPermissionLevel.Manager);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Name = "Connection", Provider = SourceControlProvider.GitHub, Status = SourceControlConnectionStatus.Connected };
        db.Add(connection); await db.SaveChangesAsync();
        var audit = new Audit(); var service = new InternalRepositoryManagementService(db, new Host(), audit, TimeProvider.System);
        Assert.True((await service.ConnectionDisconnectPlanAsync(business, user, connection.Id, default)).CanDisconnect);
        if (dependency == "internal") connection.Provider = SourceControlProvider.InternalGit;
        if (dependency is "repository" or "archived") db.SourceControlRepositories.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id,
            Name = "repo", CanonicalPath = "owner/repo", ProviderRepositoryKey = "github:42", ArchivedAt = dependency == "archived" ? DateTimeOffset.UtcNow : null });
        if (dependency == "template") db.SourceControlRepositoryTemplates.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, Name = "starter" });
        if (dependency == "policy") db.RepositoryProvisioningPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id });
        if (dependency == "request") db.RepositoryProvisioningRequests.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, Status = RepositoryProvisioningStatus.Completed });
        if (dependency == "setup") db.SourceControlOnboardingSessions.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, Status = SourceControlOnboardingStatus.AwaitingProvider });
        await db.SaveChangesAsync();
        var plan = await service.ConnectionDisconnectPlanAsync(business, user, connection.Id, default); Assert.False(plan.CanDisconnect); Assert.NotEmpty(plan.Blockers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisconnectConnectionAsync(business, user, connection.Id, new(connection.Name, 1), default));
        Assert.Equal(SourceControlConnectionStatus.Connected, (await db.SourceControlConnections.AsNoTracking().SingleAsync()).Status); Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task ConnectionRenamePersistsWithNoTrackingAndChecksRevisionAndAuthority()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); var viewer = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Manager); Seed(db, business, viewer, OrganizationPermissionLevel.Viewer);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Name = "Internal", Provider = SourceControlProvider.InternalGit,
            Mode = SourceControlConnectionMode.InternalGit, Status = SourceControlConnectionStatus.Connected };
        db.Add(connection); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var audit = new Audit(); var service = new InternalRepositoryManagementService(db, new Host(), audit, TimeProvider.System);
        var details = await service.ConnectionAsync(business, user, connection.Id, default);
        Assert.True(details.IsBusinessDefault); Assert.Equal(0, details.RepositoryCount);
        var renamed = await service.RenameConnectionAsync(business, user, connection.Id, new(" Company Git ", details.Revision), default);
        Assert.Equal("Company Git", renamed.Name); Assert.Equal(2, renamed.Revision);
        db.ChangeTracker.Clear(); Assert.Equal("Company Git", (await db.SourceControlConnections.SingleAsync()).Name);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RenameConnectionAsync(business, user, connection.Id, new("Stale", 1), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameConnectionAsync(business, viewer, connection.Id, new("Viewer", 2), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConnectionAsync(Guid.NewGuid(), user, connection.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConnectionAsync(business, user, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameConnectionAsync(business, user, connection.Id, new("bad\nname", 2), default));
        Assert.Equal(2, audit.Events.Count); Assert.All(audit.Events, e => Assert.Equal("SourceControlConnection", e.EntityType));
        Assert.All(audit.Events, e => Assert.Equal(user, e.Actor!.ApplicationUserId));
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("account", false)]
    [InlineData("installation", false)]
    [InlineData("suspended", false)]
    [InlineData("public", false)]
    [InlineData("repository", false)]
    [InlineData("disconnected", false)]
    [InlineData("unavailable", false)]
    public async Task ConnectionHealthChecksExactGitHubIdentityAndNeverChangesConnection(string scenario, bool available)
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Manager);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Name = "GitHub", Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ExistingGitHub, Status = scenario == "disconnected" ? SourceControlConnectionStatus.Disconnected : SourceControlConnectionStatus.Connected,
            SourceAccessInstallationId = 12, ProviderAccountId = "99", AccountLogin = "owner", AccountType = "User" };
        db.AddRange(connection, new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id,
            ExternalRepositoryId = "42", Owner = "owner", Name = "repo", CanonicalPath = "owner/repo", ProviderRepositoryKey = "github:42" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var host = new Host { Descriptor = new(scenario == "installation" ? 13 : 12, scenario == "account" ? 100 : 99, "owner", "User", scenario == "suspended", null),
            AvailableRepositories = [new(scenario == "repository" ? 43 : 42, "owner", "repo", "owner/repo", "https://github.com/owner/repo.git", "main", scenario != "public", false, false)],
            Fail = scenario == "unavailable" };
        var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        var result = await service.CheckConnectionAsync(business, user, connection.Id, default);
        Assert.Equal(available, result.Available);
        Assert.DoesNotContain("provider-secret", System.Text.Json.JsonSerializer.Serialize(result));
        var persisted = await db.SourceControlConnections.AsNoTracking().SingleAsync(); Assert.Equal(1, persisted.Revision); Assert.Null(persisted.LastVerifiedAt);
        if (scenario == "disconnected") Assert.Equal(0, host.Calls);
        var calls = host.Calls;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CheckConnectionAsync(Guid.NewGuid(), user, connection.Id, default));
        Assert.Equal(calls, host.Calls);
    }

    [Fact]
    public async Task InternalConnectionHealthDoesNotExposeStoragePaths()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); Seed(db, business, user, OrganizationPermissionLevel.Manager);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Provider = SourceControlProvider.InternalGit, Name = "Internal", Status = SourceControlConnectionStatus.Connected };
        db.Add(connection); await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var result = await service.CheckConnectionAsync(business, user, connection.Id, default); Assert.True(result.Available);
        Assert.DoesNotContain("private-storage-path", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ActivityIsBusinessScopedAndPaginationDoesNotRepeatNewerEvents()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); var repository = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Manager);
        AuditEvent Event(long sequence, Guid? tenant, string category = "SourceControl", string outcome = "Completed") => new()
        {
            Id = Guid.NewGuid(), Sequence = sequence, OrganizationId = tenant, Category = category, Outcome = outcome,
            EntityType = "SourceControlRepository", EntityId = repository, EventType = "SourceControl.Repository.Update",
            OccurredAt = DateTimeOffset.UtcNow, MetadataJson = "sensitive metadata", PayloadPreview = "credential material", ErrorMessage = "provider secret"
        };
        db.AuditEvents.AddRange(Event(1, business), Event(2, business, outcome: "Started"), Event(3, business),
            Event(4, Guid.NewGuid()), Event(5, null), Event(6, business, "Authentication"));
        await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var page = await service.ActivityAsync(business, user, null, null, null, 2, default);
        Assert.Equal(new long[] { 3, 2 }, page.Items.Select(e => e.Sequence)); Assert.Equal(2, page.NextBeforeSequence);
        db.AuditEvents.Add(Event(7, business)); await db.SaveChangesAsync();
        var older = await service.ActivityAsync(business, user, null, null, page.NextBeforeSequence, 2, default);
        Assert.Equal(1, Assert.Single(older.Items).Sequence); Assert.Null(older.NextBeforeSequence);
        var filtered = await service.ActivityAsync(business, user, repository, "Started", null, 25, default);
        Assert.Equal(2, Assert.Single(filtered.Items).Sequence); Assert.Null(filtered.NextBeforeSequence);
        Assert.Empty((await service.ActivityAsync(business, user, Guid.NewGuid(), null, null, 25, default)).Items);
        var json = System.Text.Json.JsonSerializer.Serialize(page);
        Assert.DoesNotContain("sensitive metadata", json); Assert.DoesNotContain("credential material", json); Assert.DoesNotContain("provider secret", json);
        // No live repository row exists: deleted repository history is still visible to its business manager.
        Assert.Empty(db.SourceControlRepositories);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("inactive")]
    [InlineData("foreign")]
    public async Task ActivityRequiresCurrentBusinessManager(string rejected)
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, rejected == "viewer" ? OrganizationPermissionLevel.Viewer : OrganizationPermissionLevel.Manager);
        await db.SaveChangesAsync();
        if (rejected == "inactive") (await db.CoreOrganizationUsers.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ActivityAsync(rejected == "foreign" ? Guid.NewGuid() : business, user, null, null, null, 25, default));
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(101, null, null)]
    [InlineData(25, 0L, null)]
    [InlineData(25, -1L, null)]
    [InlineData(25, null, "unknown")]
    public async Task ActivityRejectsUnboundedOrInvalidFilters(int pageSize, long? before, string? outcome)
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Manager); await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ActivityAsync(business, user, null, outcome, before, pageSize, default));
    }

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

    [Fact]
    public async Task BackupRestoreRetriesPersistReadyStateWithoutSourceRecordOrCopiedAccess()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); await db.SaveChangesAsync();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var host = new Host { Fail = true }; var audit = new Audit();
        var service = new InternalRepositoryManagementService(db, host, audit, TimeProvider.System);
        var source = Guid.NewGuid(); var backup = Guid.NewGuid(); var request = new RestoreInternalGitBackupRequest(Guid.NewGuid(), "recovered");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreBackupAsync(business, user, source, backup, request, default));
        db.ChangeTracker.Clear();
        Assert.Equal(SourceControlRepositoryStatus.Provisioning, (await db.SourceControlRepositories.SingleAsync()).Status);
        host.Fail = false;
        Assert.Equal("Ready", (await service.RestoreBackupAsync(business, user, source, backup, request, default)).Status);
        db.ChangeTracker.Clear();
        Assert.Equal(SourceControlRepositoryStatus.Ready, (await db.SourceControlRepositories.SingleAsync()).Status);
        Assert.Equal("trunk", (await db.SourceControlRepositories.SingleAsync()).DefaultBranch);
        Assert.Equal(request.RestoreId, (await service.RestoreBackupAsync(business, user, source, backup, request, default)).Id);
        Assert.Single(db.SourceControlRepositories); Assert.Empty(db.SourceControlCredentials);
        var calls = host.Calls;
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreBackupAsync(business, user, source, backup, request with { RestoreId = Guid.NewGuid() }, default));
        Assert.Equal(calls, host.Calls);
        Assert.All(audit.Events, e => Assert.Equal(business, e.OrganizationId));
    }

    [Fact]
    public async Task BackupOperationsRejectViewerBeforeCallingHost()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Viewer); await db.SaveChangesAsync();
        var host = new Host(); var service = new InternalRepositoryManagementService(db, host, new Audit(), TimeProvider.System);
        var source = Guid.NewGuid(); var backup = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BackupsAsync(business, user, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BackupAsync(business, user, source, new(backup), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteBackupAsync(business, user, source, backup, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreBackupAsync(business, user, source, backup, new(Guid.NewGuid(), "restore"), default));
        Assert.Equal(0, host.Calls); Assert.Empty(db.SourceControlRepositories);
    }

    [Fact]
    public async Task BusinessDefaultsAreInternalInitiallyAndPersistManagerChoiceWithRevisionChecks()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); await db.SaveChangesAsync();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        var initial = await service.BusinessDefaultsAsync(business, user, default);
        Assert.Null(initial.DefaultTemplateId); Assert.Equal(0, initial.Revision); Assert.True(Assert.Single(initial.Options).Available);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Name = "Company GitHub", Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ManagedGitHub, AccountType = "Organization", Status = SourceControlConnectionStatus.Connected, ProvisionerInstallationId = 7, SourceAccessInstallationId = 8 };
        var template = new SourceControlRepositoryTemplate { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, Name = "starter", DisplayName = "Starter" };
        db.AddRange(connection, template, new RepositoryProvisioningPolicy { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id,
            ApprovedTemplatesJson = System.Text.Json.JsonSerializer.Serialize(new[] { template.Id }), MaximumRepositories = 10 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateBusinessDefaultsAsync(business, user, new(Guid.NewGuid(), 0), default));
        var saved = await service.UpdateBusinessDefaultsAsync(business, user, new(template.Id, 0), default);
        Assert.Equal(template.Id, saved.DefaultTemplateId); Assert.Equal(1, saved.Revision);
        db.ChangeTracker.Clear(); Assert.Equal(template.Id, (await service.BusinessDefaultsAsync(business, user, default)).DefaultTemplateId);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.UpdateBusinessDefaultsAsync(business, user, new(null, 0), default));
        var reset = await service.UpdateBusinessDefaultsAsync(business, user, new(null, 1), default);
        Assert.Null(reset.DefaultTemplateId); Assert.Equal(2, reset.Revision); Assert.Empty(db.SourceControlRepositories);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BusinessDefaultsAsync(Guid.NewGuid(), user, default));
    }

    [Fact]
    public async Task DefaultsRejectViewerAndUnavailableOrUnapprovedGitHubTemplates()
    {
        await using var db = Database(); var business = Guid.NewGuid(); var user = Guid.NewGuid(); var viewer = Guid.NewGuid();
        Seed(db, business, user, OrganizationPermissionLevel.Owner); Seed(db, business, viewer, OrganizationPermissionLevel.Viewer); await db.SaveChangesAsync();
        var service = new InternalRepositoryManagementService(db, new Host(), new Audit(), TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateBusinessDefaultsAsync(business, viewer, new(null, 0), default));
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ManagedGitHub, AccountType = "User", Status = SourceControlConnectionStatus.Connected };
        var template = new SourceControlRepositoryTemplate { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, Name = "starter" };
        db.AddRange(connection, template); await db.SaveChangesAsync();
        var options = await service.BusinessDefaultsAsync(business, user, default);
        Assert.False(options.Options.Single(o => o.TemplateId == template.Id).Available);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateBusinessDefaultsAsync(business, user, new(template.Id, 0), default));
        connection.AccountType = "Organization"; connection.ProvisionerInstallationId = 7; connection.SourceAccessInstallationId = 8; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateBusinessDefaultsAsync(business, user, new(template.Id, 0), default));
        Assert.Empty(db.SourceControlBusinessSettings);
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
        public Task<InternalGitBackupSummary> RestoreInternalBackupAsync(InternalGitBackupRestoreRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; if (Fail) throw new IOException("Storage unavailable.");
            return Task.FromResult(new InternalGitBackupSummary(request.BackupId, request.RepositoryId, DateTimeOffset.UtcNow, "trunk", 22, new string('a', 64), 0, 0));
        }
        public TrustedInstallationDescriptor Descriptor { get; set; } = new(12, 99, "owner", "User", false, null);
        public IReadOnlyList<TrustedRepositoryDescriptor> AvailableRepositories { get; set; } = [];
        public Task<InternalGitStorageStatus> GetInternalStorageStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new InternalGitStorageStatus(true, "private-storage-path", "", "filesystem", "", "filesystem", "", null));
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken cancellationToken = default)
        { Calls++; if (Fail) throw new HttpRequestException("provider-secret"); return Task.FromResult(Descriptor); }
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(AvailableRepositories); }
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
