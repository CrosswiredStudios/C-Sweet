using System.Text.Json;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Core;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.BusinessOnboarding;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.PersonalAssistant;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.SourceControl;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class BusinessFirstOnboardingTests
{
    [Theory]
    [InlineData("New", "command-center")]
    [InlineData("Existing", "onboarding")]
    public async Task BusinessCreationIsIndependentOfGitHubAssistantAndChief(string mode, string destination)
    {
        await using var f = await Fixture.CreateAsync();
        var roles = new RoleService(f.Db, new Audit());
        var business = new BusinessOnboardingService(new CoreOrganizationService(f.Db, new Audit(), roles), roles,
            new StrategicObjectiveService(f.Db, new Audit()), new WorkerService(f.Db, new Audit()), new Audit(),
            new ExecutiveBriefingService(f.Db, new Audit(), TimeProvider.System), f.Db);
        var request = new StartBusinessOnboardingRequest("A second business", null, null, null, null, "same-create", null) { StartMode = mode };
        var result = await business.StartAsync(request, f.User);
        var replay = await business.StartAsync(request, f.User);
        Assert.Equal(result.OrganizationId, replay.OrganizationId);
        Assert.EndsWith(destination, result.ActionUri);
        Assert.Equal(mode, (await f.Db.BusinessOnboardingOperations.SingleAsync()).StartMode);
        Assert.Single(await f.Db.BusinessSuggestions.Where(x => x.OrganizationId == result.OrganizationId).ToListAsync());
        Assert.Empty(f.Db.AgentInstallations);
        Assert.Equal(0, f.Host.SnapshotReads);
        Assert.True(await business.ProcessNextAsync("test"));
        Assert.Equal("Succeeded", (await business.GetForUserAsync(result.Id, f.User))!.Status);
        Assert.EndsWith(destination, (await business.GetForUserAsync(result.Id, f.User))!.ActionUri);
    }

    [Fact]
    public async Task SuggestionDismissalIsBusinessWideAndSurvivesImportAndRefresh()
    {
        await using var f = await Fixture.CreateAsync();
        var suggestions = new BusinessSuggestionService(f.Db);
        await suggestions.EnsureChiefAsync(f.Org, default); await f.Db.SaveChangesAsync();
        var suggestion = Assert.Single(await suggestions.ListAsync(f.Org, f.User, default));
        await suggestions.DismissAsync(f.Org, f.User, suggestion.Id, suggestion.Revision, default);
        await suggestions.DismissAsync(f.Org, f.User, suggestion.Id, suggestion.Revision, default);
        await suggestions.EnsureChiefAsync(f.Org, default); await f.Db.SaveChangesAsync();
        Assert.Empty(await suggestions.ListAsync(f.Org, f.User, default));
        Assert.Equal("Dismissed", (await f.Db.BusinessSuggestions.SingleAsync()).Status);
        Assert.Contains(f.Db.ApplicationRealtimeOutbox, x => x.EventType == AppRealtimeEvents.SuggestionsChanged && x.OrganizationId == f.Org);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => suggestions.ListAsync(f.Org, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ActualActiveChiefAssignmentCompletesTheSuggestion()
    {
        await using var f = await Fixture.CreateAsync();
        await new BusinessSuggestionService(f.Db).EnsureChiefAsync(f.Org, default); await f.Db.SaveChangesAsync();
        var chief = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, DisplayName = "Chief", EmployeeType = EmployeeType.Agent, IsActive = true };
        f.Db.CoreOrganizationUsers.Add(chief);
        f.Db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, OrganizationUserId = chief.Id, PositionKey = "chief-of-staff", StartsAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        await f.Db.SaveChangesAsync();
        Assert.Equal("Completed", (await f.Db.BusinessSuggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task MultipleRepositoriesCreateUnstaffedProjectsWithoutDownloadingCodeAndReplaysReuseThem()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.DraftAsync(100, 101);
        var names = draft.Sources.ToDictionary(x => x.Id, x => "Imported " + x.Name);
        var submittedRevision = draft.Revision;
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, names));
        var first = await f.Service.ProjectsAsync(f.User, draft.Id);
        Assert.Equal(2, first.Count); Assert.Equal(0, f.Host.SnapshotReads);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(submittedRevision, names));
        Assert.Equal(first.Select(x => x.ProjectId).Order(), (await f.Service.ProjectsAsync(f.User, draft.Id)).Select(x => x.ProjectId).Order());
        Assert.Equal(2, await f.Db.Workstreams.CountAsync());
        Assert.All(await f.Db.Workstreams.ToListAsync(), x => { Assert.Equal(WorkstreamStatus.Proposed, x.Status); Assert.Null(x.AccountableManagerOrganizationUserId); Assert.Null(x.ProfileKey); });
        Assert.Empty(f.Db.ProjectDeliveryBindings); Assert.Empty(f.Db.OrganizationTeams); Assert.Empty(f.Db.AgentInstallations);
        var members = await new ProjectSetupService(f.Db, TimeProvider.System, new(f.Db, TimeProvider.System)).MembersAsync(f.Owner, first[0].ProjectId, default);
        Assert.Null(members.ManagerId); Assert.Null(members.TeamId);
    }

    [Fact]
    public async Task OneRevokedRepositoryDoesNotRemoveBusinessOrSuccessfulProjects()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100, 101);
        f.Host.Revoked.Add(101);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        Assert.Single(await f.Service.ProjectsAsync(f.User, draft.Id));
        Assert.NotNull(draft.Sources.Single(x => x.Name.EndsWith("repo101")).Error);
        Assert.Single(f.Db.CoreOrganizations);
        f.Host.Revoked.Clear();
        await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        Assert.Equal(2, await f.Db.Workstreams.CountAsync());
        Assert.Equal(0, f.Host.SnapshotReads);
    }

    [Fact]
    public async Task ProfilesCanBeAcceptedManuallyWithoutAssistantAndLateResultsCannotOverwriteThem()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        var project = Assert.Single(await f.Service.ProjectsAsync(f.User, draft.Id));
        var profile = await f.Db.ImportedProjectProfiles.SingleAsync();
        var workId = Guid.NewGuid(); profile.Status = "Analyzing"; profile.AgentWorkItemId = workId; profile.AnalysisRevision = profile.Revision;
        var record = await f.Db.RepositoryOnboardingDrafts.SingleAsync(); record.Status = "Analyzing"; record.AgentWorkItemId = workId;
        await f.Db.SaveChangesAsync();
        var accepted = new RepositoryProjectBrief(project.SourceId.ToString("D"), "Owner's project", "Owner reviewed purpose", "C#", "Existing product", []) { Category = "Software" };
        await f.Service.AcceptProfileAsync(f.User, draft.Id, project.ProjectId, new(profile.Revision, accepted));
        var observer = new PersonalAssistantResultObserver(f.Db);
        await observer.ObserveAsync(new AgentWorkItem { Id = workId, SourceType = "repository-onboarding-analysis", SourceId = draft.Id.ToString(), OrganizationId = PersonalAssistantCapabilities.UserScope(f.User) }, false, null, default);
        await f.Db.SaveChangesAsync();
        Assert.Equal("Accepted", profile.Status); Assert.Contains("Owner reviewed purpose", profile.AcceptedJson);
        Assert.Equal("An existing business", (await f.Db.CoreOrganizations.SingleAsync()).Name);
        Assert.Equal("Owner's project", (await f.Db.Workstreams.SingleAsync()).Name);
    }

    [Fact]
    public async Task ProfilingFailureLeavesProjectsSavedAndCrossBusinessAccessIsDenied()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.AnalyzeAsync(f.User, draft.Id, new(draft.Revision, true)));
        Assert.Single(f.Db.Workstreams); Assert.Single(f.Db.SourceControlRepositories);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetAsync(Guid.NewGuid(), draft.Id));
    }

    [Fact]
    public async Task FinishedImportRemainsResumableAndNextImportReusesAuthorizedRepositoriesAndProjects()
    {
        await using var f = await Fixture.CreateAsync(); var first = await f.DraftAsync(100);
        first = await f.Service.AddProjectsAsync(f.User, first.Id, new(first.Revision, first.Sources.ToDictionary(x => x.Id, x => x.Name)));
        var project = Assert.Single(await f.Service.ProjectsAsync(f.User, first.Id));
        await f.Service.FinishAsync(f.User, first.Id, first.Revision);
        var next = await f.Service.StartBusinessImportAsync(f.User, f.Org, null);
        Assert.NotEqual(first.Id, next.Id);
        Assert.Single(await f.Service.AccountsAsync(f.User, next.Id));
        next = await f.Service.SelectAsync(f.User, next.Id, new(next.Revision, 1, [100]));
        next = await f.Service.AddProjectsAsync(f.User, next.Id, new(next.Revision, next.Sources.ToDictionary(x => x.Id, x => "Do not rename saved project")));
        Assert.Equal(project.ProjectId, Assert.Single(await f.Service.ProjectsAsync(f.User, next.Id)).ProjectId);
        Assert.Single(f.Db.Workstreams);
        Assert.Equal(2, (await f.Service.BusinessImportsAsync(f.User, f.Org)).Count);
        Assert.Equal(first.Id, (await f.Service.StartBusinessImportAsync(f.User, f.Org, first.Id)).Id);
        var manual = new RepositoryProjectBrief(project.SourceId.ToString(), "Reviewed", "Purpose", "C#", "Unknown", []) { Category = "Software", Uncertainties = ["Delivery goal pending"] };
        await f.Service.AcceptProfileAsync(f.User, next.Id, project.ProjectId, new(project.ProfileRevision, manual));
        Assert.Equal("Accepted", Assert.Single(await f.Service.ProjectsAsync(f.User, first.Id)).ProfileStatus);
    }

    [Fact]
    public async Task GovernedSetupAdoptsImportedIdentityAndRejectsStaleRevision()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        var project = await f.Db.Workstreams.SingleAsync();
        project.AccountableManagerOrganizationUserId = f.Owner.Id;
        var plan = new CSweet.WorkManagement.Contracts.WorkstreamPlanProposalV2Request(project.Name, "Review next goals", [], "Development", f.Owner.Id,
            null, [], [], null, null, null, null, "Reviewed setup", "adopt", "software-prototype.v1", 1, JsonSerializer.SerializeToElement(new { }),
            new(null, null, [], ["execution"], [], null), [], []) { ExistingProjectId = project.Id, ExpectedProjectRevision = project.Revision };
        f.Db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = plan.ProfileKey, Version = 1, Status = "Active", DefinitionDigest = "digest",
            DefinitionJson = "{\"lifecycle\":{\"stages\":[{\"key\":\"Development\"}]}}", MetadataSchemaJson = "{\"type\":\"object\"}" });
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportedProjectAdoption.RequireAsync(f.Db, f.Org, plan with { ExpectedProjectRevision = 100 }, default));
        Assert.Single(f.Db.Workstreams); Assert.Equal(WorkstreamStatus.Proposed, project.Status);
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = f.Org, ActionType = "workstream.create.v2",
            PayloadJson = JsonSerializer.Serialize(new { profileDefinitionDigest = "digest", payload = plan }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var executor = new WorkstreamManagedActionExecutor(f.Db, TimeProvider.System);
        var result = await executor.ExecuteAsync(proposal, f.Owner); await f.Db.SaveChangesAsync();
        var replay = await executor.ExecuteAsync(proposal, f.Owner);
        Assert.Equal(project.Id, result.ResourceId); Assert.Equal(project.Id, replay.ResourceId);
        Assert.Single(f.Db.Workstreams); Assert.Single(f.Db.ProjectRepositoryAssociations);
        Assert.Equal(WorkstreamStatus.Approved, project.Status);
        Assert.Single(f.Db.WorkstreamAuthorityEnvelopes);
        Assert.Empty(f.Db.AgentInstallations); Assert.Empty(f.Db.ProjectDeliveryBindings);
    }

    [Fact]
    public async Task CallbackRemainsBoundToTheOwnerWhoStartedAuthorization()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        var other = Guid.NewGuid();
        f.Db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, ApplicationUserId = other,
            DisplayName = "Other owner", IsActive = true, PermissionLevel = OrganizationPermissionLevel.Owner });
        var stored = await f.Db.RepositoryOnboardingDrafts.SingleAsync();
        stored.GitHubAuthorizationRequestedByApplicationUserId = f.User;
        stored.GitHubStateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("nonce")));
        stored.GitHubStateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteGitHubAsync(other, draft.Id, new("nonce", 1, "code")));
        Assert.NotNull(stored.GitHubStateExpiresAt);
    }

    [Fact]
    public async Task GameImportsRetainCanonicalDirectorProducerRolesAndDedicatedManagement()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        var project = await f.Db.Workstreams.SingleAsync();
        var profile = await f.Db.ImportedProjectProfiles.SingleAsync(); profile.Status = "Accepted"; profile.AcceptedJson = "{\"category\":\"VideoGame\"}";
        OrganizationUser Person(string role, Guid reportsTo)
        {
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = role, Version = "1.0.0", ManifestJson = JsonSerializer.Serialize(new { rolePolicy = new { declaredRoleKeys = new[] { role } } }) };
            var install = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = package.Id, PackageVersion = package, IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active };
            var person = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, DisplayName = role, EmployeeType = EmployeeType.Agent, AgentInstallationId = install.Id, ReportsToOrganizationUserId = reportsTo };
            f.Db.AddRange(install, person); return person;
        }
        var director = Person("game-director", f.Owner.Id); var producer = Person("game-producer", director.Id); await f.Db.SaveChangesAsync();
        Assert.True(await ImportedProjectAdoption.CanRequestInitialStaffingAsync(f.Db, f.Org, project.Id, director, default));
        Assert.False(await ImportedProjectAdoption.CanRequestInitialStaffingAsync(f.Db, f.Org, project.Id, producer, default));
        var plan = new CSweet.WorkManagement.Contracts.WorkstreamPlanProposalV2Request(project.Name, "Agree the next game goal", [], "Concept", producer.Id,
            null, [new(director.Id, "game-director")], [], null, null, null, null, "Retain game approvals", "game-adoption", "video-game", 1, JsonSerializer.SerializeToElement(new { }),
            new(null, null, [], [], [], null), [], []) { ExistingProjectId = project.Id, ExpectedProjectRevision = project.Revision };
        Assert.Equal(project.Id, (await ImportedProjectAdoption.RequireAsync(f.Db, f.Org, plan, default)).Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ImportedProjectAdoption.RequireAsync(f.Db, f.Org, plan with { InitialSupervisors = [] }, default));
        f.Db.Workstreams.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, Name = "Already managed", Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = producer.Id });
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectSetupService(f.Db, TimeProvider.System, new(f.Db, TimeProvider.System)).ReserveManagerAsync(f.Org, producer.Id, project.Id, null, default));
    }

    [Fact]
    public async Task LostSuccessfulImportResponseCanBeReplayedAfterProfilingStarts()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        var request = new AddImportedProjectsRequest(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => "Retained project"));
        await f.Service.AddProjectsAsync(f.User, draft.Id, request);
        var stored = await f.Db.RepositoryOnboardingDrafts.SingleAsync(); stored.Status = "Analyzing"; stored.Revision++;
        var profile = await f.Db.ImportedProjectProfiles.SingleAsync(); profile.Status = "Analyzing";
        await f.Db.SaveChangesAsync();
        var replay = await f.Service.AddProjectsAsync(f.User, draft.Id, request);
        Assert.Equal("Analyzing", replay.Status); Assert.Equal("Analyzing", profile.Status);
        Assert.Equal("Retained project", (await f.Db.Workstreams.SingleAsync()).Name);
        Assert.Single(f.Db.ProjectRepositoryAssociations); Assert.Equal(0, f.Host.SnapshotReads);
    }

    [Fact]
    public async Task StalePreparationFailureCannotInvalidateAnAlreadyCommittedAnalysisJob()
    {
        await using var f = await Fixture.CreateAsync(); var draft = await f.DraftAsync(100);
        draft = await f.Service.AddProjectsAsync(f.User, draft.Id, new(draft.Revision, draft.Sources.ToDictionary(x => x.Id, x => x.Name)));
        var stored = await f.Db.RepositoryOnboardingDrafts.SingleAsync(); var work = Guid.NewGuid();
        stored.Status = "Analyzing"; stored.AgentWorkItemId = work; stored.LeaseOwner = "earlier-worker";
        var profile = await f.Db.ImportedProjectProfiles.SingleAsync(); profile.Status = "Analyzing"; profile.AgentWorkItemId = work;
        await f.Db.SaveChangesAsync();
        await f.Workflow.ProcessAsync(new RepositoryOnboardingDraft { Id = stored.Id, OrganizationId = f.Org, ApplicationUserId = f.User, Status = "Preparing" }, default);
        f.Db.ChangeTracker.Clear();
        var retained = await f.Db.RepositoryOnboardingDrafts.SingleAsync();
        Assert.Equal("Analyzing", retained.Status); Assert.Equal(work, retained.AgentWorkItemId); Assert.Null(retained.LeaseOwner);
        Assert.Equal("Analyzing", (await f.Db.ImportedProjectProfiles.SingleAsync()).Status);
    }

    [Fact]
    public async Task OAuthReconnectReusesSavedAppAndDiscoversOnlyUserAuthorizedInstallations()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.Service.StartBusinessImportAsync(f.User, f.Org, null);
        var authorization = await f.Service.AuthorizeGitHubAsync(f.User, draft.Id);
        Assert.StartsWith("https://github.com/login/oauth/authorize?client_id=Iv1.saved&state=", authorization.AuthorizationUrl);
        Assert.Equal(0, f.Platform.InstallLinkReads);
        var connected = await f.Service.CompleteGitHubAsync(f.User, draft.Id, new(authorization.State, 0, "once"));
        Assert.Equal(f.Org, connected.OrganizationId); Assert.Equal(draft.Id, connected.Id);
        Assert.Equal(1, f.GitHub.DiscoveryCalls);
        var accounts = await f.Service.AccountsAsync(f.User, draft.Id);
        Assert.Single(accounts); Assert.Equal(1, accounts[0].InstallationId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteGitHubAsync(f.User, draft.Id, new(authorization.State, 0, "replay")));
        Assert.Equal(1, f.GitHub.DiscoveryCalls);
        Assert.Empty(f.Db.SourceControlConnections); Assert.Empty(f.Db.Workstreams);
    }

    [Fact]
    public async Task OAuthWithoutInstallationsRetainsBusinessAndOffersSavedAppInstallation()
    {
        await using var f = await Fixture.CreateAsync(); f.GitHub.Installations = [];
        var draft = await f.Service.StartBusinessImportAsync(f.User, f.Org, null);
        var auth = await f.Service.AuthorizeGitHubAsync(f.User, draft.Id);
        await f.Service.CompleteGitHubAsync(f.User, draft.Id, new(auth.State, 0, "once"));
        Assert.Empty(await f.Service.AccountsAsync(f.User, draft.Id));
        Assert.Equal("https://github.com/apps/saved-app/installations/new", (await f.Service.GitHubInstallationLinkAsync(f.User, draft.Id)).InstallationUrl);
        Assert.Single(f.Db.CoreOrganizations); Assert.Empty(f.Db.Workstreams);
    }

    private sealed class SavedPlatform : ISourceControlPlatformConfigurationProvider
    {
        public int InstallLinkReads;
        public Task<CSweet.Contracts.SourceControl.SourceControlPlatformReadiness> GetReadinessAsync(CancellationToken ct = default) => Task.FromResult(new CSweet.Contracts.SourceControl.SourceControlPlatformReadiness(true, false, null));
        public Task<string> GetInstallUrlAsync(PlatformGitHubAppKind kind, CancellationToken ct = default) { InstallLinkReads++; return Task.FromResult("https://github.com/apps/saved-app/installations/new"); }
        public Task<PlatformGitHubUserAuthorizationConfiguration> GetUserAuthorizationAsync(PlatformGitHubAppKind kind, CancellationToken ct = default) => Task.FromResult(new PlatformGitHubUserAuthorizationConfiguration("Iv1.saved", "secret"));
    }
    private sealed class GitHubUser : IGitHubUserAuthorizationClient
    {
        public IReadOnlyList<GitHubAuthorizedInstallation> Installations = [new(1, 99, "owner")]; public int DiscoveryCalls;
        public Task<IReadOnlyList<GitHubAuthorizedInstallation>> ListAuthorizedInstallationsAsync(PlatformGitHubUserAuthorizationConfiguration config, string code, CancellationToken ct = default) { DiscoveryCalls++; return Task.FromResult(Installations); }
        public Task<GitHubAuthorizedInstallation> VerifyInstallationAsync(PlatformGitHubUserAuthorizationConfiguration config, string code, long id, CancellationToken ct = default) => Task.FromResult(Installations.Single(x => x.InstallationId == id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid User { get; } = Guid.NewGuid(); public Guid Org { get; } = Guid.NewGuid();
        public OrganizationUser Owner { get; private set; } = null!;
        public SavedPlatform Platform { get; } = new(); public GitHubUser GitHub { get; } = new();
        public Host Host { get; } = new(); public RepositoryOnboardingWorkflow Workflow { get; private set; } = null!; public RepositoryOnboardingService Service { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.Db.Users.Add(new ApplicationUser { Id = f.User, DisplayName = "Owner" });
            f.Db.CoreOrganizations.Add(new Organization { Id = f.Org, Name = "An existing business", Status = OrganizationStatus.Active });
            f.Owner = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, ApplicationUserId = f.User, DisplayName = "CEO", IsActive = true, PermissionLevel = OrganizationPermissionLevel.Owner, EmployeeType = EmployeeType.Human };
            f.Db.CoreOrganizationUsers.Add(f.Owner); await f.Db.SaveChangesAsync();
            var inbox = new AgentWorkInbox(f.Db, new EphemeralDataProtectionProvider(), TimeProvider.System);
            var workflow = f.Workflow = new RepositoryOnboardingWorkflow(f.Db, null!, f.Host, new UnavailableAssistant(), inbox, null!,
                new InternalRepositoryManagementService(f.Db, f.Host, new Audit(), TimeProvider.System), NullLogger<RepositoryOnboardingWorkflow>.Instance);
            f.Service = new(f.Db, null!, f.Platform, f.Host, f.GitHub, new UnavailableAssistant(), inbox, workflow);
            return f;
        }
        public async Task<RepositoryOnboardingDraftResponse> DraftAsync(params long[] repos)
        {
            var response = await Service.StartBusinessImportAsync(User, Org, null);
            var draft = await Db.RepositoryOnboardingDrafts.SingleAsync(x => x.Id == response.Id); draft.VerifiedInstallationsJson = "[1]";
            await Db.SaveChangesAsync();
            return await Service.SelectAsync(User, draft.Id, new(draft.Revision, 1, repos));
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Host : ITrustedSourceControlHostClient
    {
        public int SnapshotReads; public HashSet<long> Revoked { get; } = [];
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken ct = default) => Task.FromResult(new TrustedInstallationDescriptor(id, 2, "owner", "User", false, null));
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TrustedRepositoryDescriptor>>(new long[] { 100, 101 }.Where(x => !Revoked.Contains(x)).Select(x => new TrustedRepositoryDescriptor(x, "owner", "repo" + x, "owner/repo" + x, "https://github.com/owner/repo" + x + ".git", "main", true, false, false)).ToArray());
        public Task<TrustedWorkspaceSnapshot> PrepareOnboardingSnapshotAsync(TrustedWorkspaceSnapshotRequest r, CancellationToken ct = default) { SnapshotReads++; throw new IOException("Code download unavailable"); }
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class UnavailableAssistant : IPersonalAssistantService
    {
        public Task<Guid> RequireInstallationAsync(Guid user, CancellationToken token = default) => throw new InvalidOperationException("Assistant unavailable; projects are already saved.");
        public Task<PersonalAssistantSettings> GetSettingsAsync(Guid user, CancellationToken token = default) => throw new NotSupportedException();
        public Task<PersonalAssistantSettings> ConfigureAsync(Guid user, ConfigurePersonalAssistantRequest r, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalAssistantMessageResponse>> ListMessagesAsync(Guid user, Guid? draftId = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<PersonalAssistantMessageResponse> SendAsync(Guid user, SendPersonalAssistantMessageRequest r, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string a, string b, Guid? c, string? d, string? e = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
    }
}
