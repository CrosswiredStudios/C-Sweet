using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.UnitTests;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using W = CSweet.WorkManagement.Contracts;
using C = CSweet.Contracts.WorkManagement;

namespace CSweet.GameRoleWorkflow.Tests;

// An approved game project with a staffed, empty board. Board writes, readiness, dispatch and
// the worker lease use the production services. The trusted Git remote is the existing test double.
internal sealed class FreshTeamSprintPlatform(bool useSoftwareDeveloper = false) : IAsyncDisposable
{
    private readonly HierarchicalDeliveryServiceTests.Fixture studio = new(false,
        new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    private readonly Dictionary<Guid, AgentCoordinationParticipant> people = [];
    private readonly McpAgentSessionServiceTests.MutableTimeProvider clock = new(DateTimeOffset.UtcNow);
    private McpAgentSession session = null!;
    private readonly EphemeralDataProtectionProvider protection = new();
    private WorkManagementCapabilityHandler work = null!;
    private ArtifactCapabilityHandler artifacts = null!;
    public Guid PitchId { get; } = Guid.NewGuid();
    public Guid RevisionId { get; } = Guid.NewGuid();
    public Guid PackageId { get; } = Guid.NewGuid();
    private WorkOrchestrator Dispatcher(AgentWorkInbox inbox) => new(studio.Db, inbox, new AgentAttentionInvalidationService(Db, clock),
        new WorkDispatchRecoveryTests.Runtime(studio.Db), [], clock, new DispatchLogger(), studio.Client);

    private sealed class DispatchLogger : ILogger<WorkOrchestrator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (level >= LogLevel.Error) throw new InvalidOperationException(format(state, exception), exception);
        }
    }

    public CSweetDbContext Db => studio.Db;
    public Guid Organization => studio.Org;
    public Guid Project => studio.Project;
    public Guid Team => studio.Board.TeamId!.Value;
    public Guid Board => studio.Board.Id;
    public Guid Repository => studio.Repositories.Single();
    public AgentCoordinationParticipant Gabriel => people[studio.Manager.Id];
    public AgentCoordinationParticipant Victor => people[studio.Technical.Id];
    public AgentCoordinationParticipant Engineer => people[studio.Author.Id];
    public AgentTeamContext Roster { get; private set; } = null!;
    public string ProfileDigest { get; private set; } = null!;
    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static readonly string[] BoardActions = [C.WorkBoardActions.Read, C.WorkBoardActions.Configure,
        C.WorkBoardActions.ConfigureColumns, C.WorkItemActions.Read, C.WorkItemActions.Create, C.WorkItemActions.RevisePlanning,
        C.WorkItemActions.FinalizeDelivery, C.WorkItemActions.Estimate, C.WorkItemActions.Move, C.WorkItemActions.ReadComments,
        C.WorkSprintActions.Read, C.WorkSprintActions.Create, C.WorkSprintActions.ManageCapacity, C.WorkSprintActions.ManageScope,
        C.WorkOrchestrationActions.ConfigureProfile, C.WorkOrchestrationActions.Preflight, C.WorkOrchestrationActions.Start,
        C.WorkOrchestrationActions.Read, C.WorkOrchestrationActions.Retry];

    public async Task OpenAsync(string pitch)
    {
        await studio.Seed(includeWorkItems: false);
        using var stream = typeof(FreshTeamSprintPlatform).Assembly.GetManifestResourceStream("first-sprint-profile.json")!;
        using var reader = new StreamReader(stream);
        var definition = await reader.ReadToEndAsync();
        var profile = JsonDocument.Parse(definition).RootElement;
        ProfileDigest = Digest(definition);
        var project = await Db.Workstreams.SingleAsync();
        const string vision = "video-game.vision.v1";
        Db.CoreArtifacts.Add(new() { Id = PitchId, OrganizationId = Organization, WorkstreamId = Project, TeamId = Team,
            Title = "Game pitch", DocumentType = vision, DocumentStatus = ArtifactDocumentStatus.Approved, LatestRevisionId = RevisionId, AcceptedRevisionId = RevisionId,
            Revisions = [new() { Id = RevisionId, Number = 1, OrganizationId = Organization, Content = pitch, ContentSha256 = Digest(pitch), Status = ArtifactRevisionStatus.Accepted }] });
        Db.ArtifactPackages.Add(new() { Id = PackageId, OrganizationId = Organization, WorkstreamId = Project, TeamId = Team,
            Name = "Approved game", PackageType = "video-game.production", Status = ArtifactDocumentStatus.Approved, AcceptedAt = DateTimeOffset.UtcNow,
            Members = [new() { Id = Guid.NewGuid(), ArtifactId = PitchId, AcceptedRevisionId = RevisionId, RequiredDocumentType = vision }] });
        project.ProfileKey = profile.GetProperty("key").GetString();
        project.ProfileVersion = profile.GetProperty("version").GetInt32();
        project.ProfileDefinitionDigest = ProfileDigest;
        project.Name = "First playable"; project.Outcome = pitch;
        studio.Board.ProfileKey = profile.GetProperty("defaultBoardProfileKey").GetString()!;
        Db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = project.ProfileKey!, Version = project.ProfileVersion.Value,
            DefinitionJson = definition, DefinitionDigest = ProfileDigest, DefaultBoardProfileKey = studio.Board.ProfileKey, MetadataSchemaJson = "{}", Status = "Active" });
        Db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Organization, WorkstreamId = Project, TeamId = Team, StartsAt = DateTimeOffset.UtcNow });
        var team = await Db.OrganizationTeams.SingleAsync();
        var members = new List<AgentTeammate>();
        foreach (var (person, name, role) in new[] { (studio.Manager, "Gabriel", "game-producer"),
            (studio.Technical, "Victor", "game-technical-director"), (studio.Author, useSoftwareDeveloper ? "Daniel Kim" : "Adrian", useSoftwareDeveloper ? "software-developer" : "game-engineer"), (studio.Qa, "Quinn", "game-quality-assurance") })
        {
            var installation = Guid.NewGuid();
            person.DisplayName = name; person.AgentInstallationId = installation; person.EmployeeType = EmployeeType.Agent;
            using var manifestStream = typeof(FreshTeamSprintPlatform).Assembly.GetManifestResourceStream(role + ".json")!;
            using var manifestDoc = await JsonDocument.ParseAsync(manifestStream);
            var manifest = manifestDoc.RootElement;
            var capabilities = manifest.GetProperty("provides").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();
            var policy = manifest.GetProperty("rolePolicy");
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), Version = manifest.GetProperty("version").GetString()!, AgentId = manifest.GetProperty("id").GetString()!,
                PackageSource = new() { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/" + role },
                ManifestJson = manifest.GetRawText() };
            Db.AgentInstallations.Add(new() { Id = installation, InstallationKey = Guid.NewGuid(), BusinessId = Organization.ToString(),
                PackageVersion = package, Schedule = new() { Id = Guid.NewGuid(), IsEnabled = true },
                Grant = new() { Id = Guid.NewGuid(), ProvidedCapabilitiesJson = JsonSerializer.Serialize(capabilities) } });
            people[person.Id] = new(person.Id, installation, name, role);
            members.Add(new(person.Id.ToString(), name, "Agent", null, null, "Peer", "Online") { AgentInstallationId = installation,
                IsAvailable = true, RuntimeEligibility = "Eligible", DeclaredRoleKeys = policy.GetProperty("declaredRoleKeys").Deserialize<string[]>()!,
                SpecializationKeys = policy.TryGetProperty("specializationKeys", out var strengths) ? strengths.Deserialize<string[]>()! : [], EffectiveCapabilities = capabilities });
            foreach (var action in person == studio.Manager ? BoardActions : [C.WorkItemActions.Read, C.WorkBoardActions.Read, C.WorkSprintActions.Read])
                Grant(installation, action, GrantScopeKind.Team, Team);
            Grant(installation, CSweet.Contracts.Core.ArtifactActions.Read, GrantScopeKind.Artifact, PitchId);
            if (person == studio.Manager)
                foreach (var action in W.WorkDeliveryCapabilities.All) Grant(installation, action, GrantScopeKind.Workstream, Project);
        }
        Roster = new(Team.ToString(), "game", "Game", team.Revision, Gabriel.OrganizationUserId.ToString(), "Gabriel", members, [], members.Count, false);
        await Db.SaveChangesAsync();
        var authorization = new ScopedActionAuthorizationService(Db);
        artifacts = new(Db, new ArtifactDocumentService(Db, new TestAuditEventWriter(), TimeProvider.System), new TestAuditEventWriter(), TimeProvider.System);
        work = new(Db, authorization, new TestAuditEventWriter(), new WorkOrchestrationService(Db, TimeProvider.System, authorization, studio.Client));
        await InvokeAsync(Gabriel, C.WorkOrchestrationActions.ConfigureProfile, JsonSerializer.SerializeToElement(
            new W.ConfigureProfileOrchestrationRequest(Project, Board, studio.Board.Revision, ProfileDigest, "initial-profile"), JsonSerializerOptions.Web));
    }

    private void Grant(Guid installation, string action, GrantScopeKind kind, Guid scope) =>
        Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = Organization, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation, Action = action, ScopeKind = kind, ScopeId = scope });

    public void Connect(AgentTestRuntime runtime, AgentCoordinationParticipant actor)
    {
        foreach (var action in new[] { PlatformCapabilities.ArtifactRead, PlatformCapabilities.ArtifactPackageRead })
            runtime.RegisterCapability<JsonElement, JsonElement>(action, (input, _) => InvokeAsync(actor, action, input));
        foreach (var action in actor == Gabriel ? BoardActions : [C.WorkItemActions.Read, C.WorkBoardActions.Read, C.WorkSprintActions.Read])
            runtime.RegisterCapability<JsonElement, JsonElement>(action, (input, _) => InvokeAsync(actor, action, input));
        runtime.RegisterCapability<W.ReadWorkDeliveryPlansRequest, IReadOnlyList<W.WorkDeliveryPlanResponse>>(W.WorkDeliveryCapabilities.Read,
            (r, ct) => studio.Service.ReadAsync(Organization, actor.OrganizationUserId, r, ct));
        if (actor == Gabriel)
        {
            runtime.RegisterCapability<W.ConfigureWorkDeliveryPlanRequest, W.WorkDeliveryPlanResponse>(W.WorkDeliveryCapabilities.Configure,
                (r, ct) => studio.Service.ConfigureAsync(Organization, actor.OrganizationUserId, r, ct));
            runtime.RegisterCapability<W.ControlWorkDeliveryPlanRequest, W.WorkDeliveryPlanResponse>(W.WorkDeliveryCapabilities.Control,
                (r, ct) => studio.Service.ControlAsync(Organization, actor.OrganizationUserId, r, ct));
        }
    }

    private async Task<JsonElement> InvokeAsync(AgentCoordinationParticipant actor, string action, JsonElement input)
    {
        var session = new AgentSession("first-sprint", actor.Role, actor.AgentInstallationId.ToString(), Organization.ToString(),
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { action }, 1));
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString(), Capability = action, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(input)) };
        IPlatformCapabilityHandler handler = artifacts.CanHandle(action) ? artifacts : work;
        await foreach (var response in handler.HandleAsync(session, request, default))
        {
            Assert.True(response.Succeeded, $"{actor.DisplayName} {action}: {response.Error}");
            return JsonSerializer.Deserialize<JsonElement>(response.Payload.Span);
        }
        throw new InvalidOperationException("Missing broker response to " + action);
    }

    public async Task<ClaimedAgentWork?> ClaimAsync()
    {
        // A new process represents reconnect. The inbox and orchestrator keep their state in the database.
        var process = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = Engineer.AgentInstallationId,
            QueuedAt = DateTimeOffset.UtcNow, RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        Db.AgentRuntimeInstances.Add(process);
        await Db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(Db, protection, clock);
        await Dispatcher(inbox).PulseAsync();
        session = new McpAgentSession { Id = Guid.NewGuid(), OrganizationId = Organization.ToString(),
            AgentInstallationId = Engineer.AgentInstallationId, RuntimeInstanceId = process.Id, TickId = process.TickId };
        return await inbox.ClaimAsync(session, default);
    }

    public async Task ReportTimeoutAsync(ClaimedAgentWork lease, string command)
    {
        var inbox = new AgentWorkInbox(Db, protection, clock);
        var digest = Digest(command).ToUpperInvariant();
        await inbox.AppendProgressAsync(session, lease.WorkId, lease.Attempt, lease.LeaseToken, 1,
            JsonSerializer.SerializeToElement(new { kind = "workspace-tool", phase = "started", commandDigest = digest, scriptCommand = command }), default);
        await inbox.CompleteAsync(session, lease.WorkId, lease.Attempt, lease.LeaseToken,
            new(false, null, "workspace command exceeded its deadline", "workspace.command.timeout", true), default);
        await Dispatcher(inbox).PulseAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
    }

    public ValueTask DisposeAsync() => studio.DisposeAsync();
}

