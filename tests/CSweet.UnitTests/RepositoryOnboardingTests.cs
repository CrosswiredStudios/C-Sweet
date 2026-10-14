using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.PersonalAssistant;
using CSweet.Infrastructure.Setup;
using CSweet.TrustedServices;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.BusinessOnboarding;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class RepositoryOnboardingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "csweet-onboarding-tests", Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private RepositorySourceStore Store(ITrustedSourceControlHostClient? host = null) => new(Options.Create(new RepositoryOnboardingOptions { StagingPath = root }), host ?? new SnapshotHost());

    [Fact]
    public async Task ZipPreservesAssetsButAnalysisExcludesSensitiveGeneratedAndBinaryFiles()
    {
        using var zip = Archive(("game/README.md", "# Moss Studio\nA cozy exploration game."), ("game/.env", "TOKEN=hidden"),
            ("game/src/game.gd", "extends Node"), ("game/node_modules/pkg/index.ts", "dependency"), ("game/assets/image.bin", "\0binary"));
        var user = Guid.NewGuid(); var draft = Guid.NewGuid();
        var staged = await Store().StageUploadAsync(user, draft, Guid.NewGuid(), "game.zip", zip, default);
        Assert.Contains(staged.Input.Files, x => x.Path == "README.md");
        Assert.Contains(staged.Input.Files, x => x.Path == "src/game.gd");
        Assert.DoesNotContain(staged.Input.Files, x => x.Path.Contains(".env") || x.Path.Contains("node_modules") || x.Path.EndsWith(".bin"));
        await using var stored = await Store().OpenAsync(user, draft, staged.ArchiveKey, default);
        using var complete = new ZipArchive(stored, ZipArchiveMode.Read);
        Assert.Contains(complete.Entries, x => x.FullName == "assets/image.bin");
        Assert.Contains(complete.Entries, x => x.FullName == ".env");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Store().OpenAsync(Guid.NewGuid(), draft, staged.ArchiveKey, default));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("repo/.git/config")]
    public async Task UnsafeArchiveEntriesAreRejected(string path)
    {
        using var zip = Archive((path, "unsafe"));
        await Assert.ThrowsAnyAsync<Exception>(() => Store().StageUploadAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "repo.zip", zip, default));
        Assert.False(File.Exists(Path.Combine(root, "escape.md")));
    }

    [Fact]
    public async Task GitHubSnapshotsRetainTheirActualSingleTopLevelDirectory()
    {
        using var zip = Archive(("docs/README.md", "# Existing project"));
        var staged = await Store(new SnapshotHost(zip.ToArray())).PrepareGitHubAsync(Guid.NewGuid(), Guid.NewGuid(),
            new RepositoryOnboardingSource { Id = Guid.NewGuid(), InstallationId = 10, ExternalRepositoryId = 20, Owner = "owner", Name = "project" }, default);
        Assert.Equal("docs/README.md", Assert.Single(staged.Input.Files).Path);
        Assert.Equal(new string('a', 40), staged.Input.Snapshot);
    }

    [Fact]
    public void BriefRequiresEverySourceAndActualSnapshotEvidence()
    {
        var input = new RepositoryAnalysisInput("one", "Game", "snapshot", [new("README.md", "game")], []);
        var project = new RepositoryProjectBrief("one", "Game", "Summary", "Godot", "Prototype", [new("one", "README.md", "snapshot")]);
        RepositoryBriefValidator.ValidateEvidence([project], [input]);
        Assert.Throws<ArgumentException>(() => RepositoryBriefValidator.ValidateEvidence([project with { Evidence = [new("one", "invented.md", "snapshot")] }], [input]));
        Assert.Throws<ArgumentException>(() => RepositoryBriefValidator.ValidateEvidence([project with { Evidence = [new("one", "README.md", "old")] }], [input]));
        Assert.Throws<ArgumentException>(() => RepositoryBriefValidator.ValidateEvidence([], [input]));
        Assert.Throws<ArgumentException>(() => RepositoryBriefValidator.ValidateEvidence([null!], [input]));
    }

    [Fact]
    public async Task LateAnalysisCannotOverwriteManualReviewAndEventsAreUserScoped()
    {
        await using var db = Database(); var user = Guid.NewGuid(); var workId = Guid.NewGuid();
        var draft = new RepositoryOnboardingDraft { Id = Guid.NewGuid(), ApplicationUserId = user, Status = "Review", AgentWorkItemId = workId,
            ReviewJson = "{\"businessName\":\"Owner's name\"}" };
        db.RepositoryOnboardingDrafts.Add(draft); await db.SaveChangesAsync();
        var observer = new PersonalAssistantResultObserver(db);
        var work = new AgentWorkItem { Id = workId, SourceType = "repository-onboarding-analysis", SourceId = draft.Id.ToString(), OrganizationId = PersonalAssistantCapabilities.UserScope(user) };
        await observer.ObserveAsync(work, false, null, default);
        Assert.Equal("Review", draft.Status); Assert.Empty(db.ApplicationRealtimeOutbox);
        draft.Status = "Analyzing";
        await observer.ObserveAsync(work, false, null, default); await db.SaveChangesAsync();
        Assert.Equal("AnalysisFailed", draft.Status);
        var notification = Assert.Single(db.ApplicationRealtimeOutbox);
        Assert.Equal(user, notification.RecipientApplicationUserId); Assert.Null(notification.OrganizationId);
        Assert.Equal(AppRealtimeEvents.RepositoryOnboardingChanged, notification.EventType);
    }

    [Fact]
    public async Task WrongUserScopeAndMalformedRepliesNeverLeakOrBreakCompletion()
    {
        await using var db = Database(); var user = Guid.NewGuid(); var workId = Guid.NewGuid();
        var answer = new PersonalAssistantMessage { Id = Guid.NewGuid(), ApplicationUserId = user, Role = "assistant", Status = "Pending", AgentWorkItemId = workId, IdempotencyKey = "answer:one" };
        db.PersonalAssistantMessages.Add(answer); await db.SaveChangesAsync();
        var observer = new PersonalAssistantResultObserver(db);
        var work = new AgentWorkItem { Id = workId, SourceType = "personal-assistant-message", SourceId = answer.Id.ToString(), OrganizationId = PersonalAssistantCapabilities.UserScope(Guid.NewGuid()) };
        await observer.ObserveAsync(work, true, JsonSerializer.SerializeToElement(new { message = "private" }), default);
        Assert.Equal("Pending", answer.Status); Assert.Empty(db.ApplicationRealtimeOutbox);
        work.OrganizationId = PersonalAssistantCapabilities.UserScope(user);
        await observer.ObserveAsync(work, true, JsonSerializer.SerializeToElement("wrong shape"), default);
        Assert.Equal("Failed", answer.Status);
    }

    [Fact]
    public async Task UserAgentUsesStandardConfigurationWithoutBusinessEmployee()
    {
        await using var db = Database(); var user = Guid.NewGuid(); var provider = Guid.NewGuid(); var installation = Guid.NewGuid();
        var manifest = BundledPersonalAssistantInstaller.ReadManifest();
        db.Users.Add(new ApplicationUser { Id = user });
        db.LlmProviderProfiles.Add(new LlmProviderProfile { Id = provider, Name = "Local", IsEnabled = true, DefaultChatModel = "model" });
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = PersonalAssistantCapabilities.AgentId, ManifestJson = JsonSerializer.Serialize(manifest, Json),
            PackageSource = new AgentPackageSource { Id = Guid.NewGuid(), IsBundled = true } };
        var record = new AgentInstallation { Id = installation, BusinessId = PersonalAssistantCapabilities.UserScope(user), Scope = PluginInstallationScope.User, OwnerApplicationUserId = user,
            IsEnabled = true, PackageVersion = package, Configuration = new AgentInstallationConfiguration { Id = Guid.NewGuid(), AgentInstallationId = installation, SettingsJson = "{}", Revision = 1 } };
        db.AgentInstallations.Add(record); db.PersonalAssistantProfiles.Add(new PersonalAssistantProfile { ApplicationUserId = user, AgentInstallationId = installation });
        await db.SaveChangesAsync();
        Assert.True(await UserScopedAgentPolicy.IsAuthorizedAsync(db, installation, record.BusinessId, default));
        Assert.False(await UserScopedAgentPolicy.IsAuthorizedAsync(db, installation, PersonalAssistantCapabilities.UserScope(Guid.NewGuid()), default));
        var config = new AgentInstallationConfigurationService(db, new NoAudit());
        var values = new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(provider.ToString()), ["llmModel"] = JsonSerializer.SerializeToElement("model"),
            ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(65536), ["maxOutputTokens"] = JsonSerializer.SerializeToElement(8192) };
        var saved = await config.SaveAsync(installation, "1.0", values);
        Assert.Equal("model", saved.Settings["llmModel"].GetString()); Assert.Equal(2, record.Configuration.Revision);
        Assert.Empty(db.CoreOrganizations); Assert.Empty(db.CoreOrganizationUsers);
        var resolved = await config.ResolveInstallationAsync(installation);
        Assert.Equal("model", resolved.Settings["llmModel"].GetString());
        values["maxOutputTokens"] = JsonSerializer.SerializeToElement(65536);
        await Assert.ThrowsAsync<AgentInstallationException>(() => config.SaveAsync(installation, "1.0", values));
        record.PackageVersion.PackageSource!.IsBundled = false; await db.SaveChangesAsync();
        Assert.False(await UserScopedAgentPolicy.IsAuthorizedAsync(db, installation, record.BusinessId, default));
    }

    [Fact]
    public void BundledSourceContainsIndependentSdkProjectAndNoBuildOutputs()
    {
        using var archive = BundledPersonalAssistantInstaller.CreateSourceArchive(); using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("csweet-plugin.json"));
        Assert.NotNull(zip.GetEntry("src/CSweet.PersonalAssistant/CSweet.PersonalAssistant.csproj"));
        Assert.DoesNotContain(zip.Entries, x => x.FullName.Contains("/bin/") || x.FullName.Contains("/obj/"));
    }

    [Fact]
    public async Task FirstRunInstallsBundledSdkAgentThroughStandardPipelineAndQueuesReplaySafeChat()
    {
        await using var db = Database(); var user = Guid.NewGuid(); var provider = Guid.NewGuid();
        await new SetupService(db).EnsureSeededAsync();
        db.Users.Add(new ApplicationUser { Id = user, IsInitialAdministrator = true });
        db.LlmProviderProfiles.Add(new LlmProviderProfile { Id = provider, Name = "Local", IsEnabled = true, DefaultChatModel = "model" });
        await db.SaveChangesAsync();
        var options = Options.Create(new AgentRuntimeManagerOptions { SourceArchiveStorePath = Path.Combine(root, "agent-sources") });
        var installationService = new AgentInstallationService(db, new NoAudit(), null!, null!, options, NullLogger<AgentInstallationService>.Instance);
        var installer = new BundledPersonalAssistantInstaller(db, new PluginArchiveImportService(db, new PluginManifestReader(), new NoAudit(), options), installationService);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var config = new AgentInstallationConfigurationService(db, new NoAudit(), inbox);
        var scheduler = new Scheduler();
        var service = new PersonalAssistantService(db, installer, config, config, inbox, scheduler);
        var settings = await service.ConfigureAsync(user, new(provider, "model", 0, SetInstallationDefault: true, Name: "  Nova  "));
        Assert.True(settings.Ready); Assert.NotNull(settings.InstallationId);
        Assert.Equal("Nova", settings.Name);
        Assert.Equal(256000, settings.Settings["maxContextWindowTokens"].GetInt32());
        Assert.Equal(128000, settings.Settings["maxOutputTokens"].GetInt32());
        var renamed = await service.ConfigureAsync(user, new(provider, "model", settings.Revision, Name: "Atlas"));
        Assert.Equal("Atlas", (await service.GetSettingsAsync(user)).Name);
        Assert.True(renamed.Revision > settings.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ConfigureAsync(user, new(provider, "model", settings.Revision, Name: "Stale")));
        var preserved = await service.ConfigureAsync(user, new(provider, "model", renamed.Revision, 64000, 8000));
        Assert.Equal("Atlas", preserved.Name);
        Assert.Equal(64000, preserved.Settings["maxContextWindowTokens"].GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(user, new(provider, "model", Name: " ")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfigureAsync(Guid.NewGuid(), new(provider, "model")));
        var installed = Assert.Single(db.AgentInstallations);
        Assert.Equal(PluginInstallationScope.User, installed.Scope); Assert.Equal(user, installed.OwnerApplicationUserId);
        Assert.Equal(PersonalAssistantCapabilities.AgentId, installed.PackageVersion!.AgentId);
        Assert.Equal("1.0", installed.Configuration!.SchemaVersion);
        Assert.Single(db.AgentBuildJobs); Assert.Empty(db.CoreOrganizations); Assert.Empty(db.CoreOrganizationUsers);
        Assert.False(Assert.Single(db.AgentRuntimeGlobalSettings).EnableImportedAgents);
        Assert.Equal(provider, Assert.Single(db.SystemConfigurations).PersonalAssistantProviderId);
        var message = await service.SendAsync(user, new("Help me start", "request-one"));
        var replay = await service.SendAsync(user, new("Help me start", "request-one"));
        Assert.Equal(message.Id, replay.Id); Assert.Equal(2, db.PersonalAssistantMessages.Count());
        Assert.Single(db.AgentWorkItems); Assert.Equal(installed.Id, scheduler.Installation);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ConfigureAsync(user, new(provider, "changed-model", 0)));
        var eligibility = new AgentRuntimeEligibilityService(db, config);
        Assert.False((await eligibility.EvaluateAsync(installed.Id)).IsEligible);
        installed.PackageVersion.Status = AgentPackageVersionStatus.Built; installed.PackageVersion.PackageDigest = new string('a', 64); installed.PackageVersion.ArtifactSignature = "signed";
        await db.SaveChangesAsync();
        Assert.True((await eligibility.EvaluateAsync(installed.Id)).IsEligible);
        installed.OwnerApplicationUserId = Guid.NewGuid(); await db.SaveChangesAsync();
        Assert.False((await eligibility.EvaluateAsync(installed.Id)).IsEligible);
    }

    [Fact]
    public async Task ZipImportMakesOneCommitAndReplayCannotReplaceRepository()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".csweet-git-store"), "onboarding-test");
        var options = new InternalGitStorageOptions { RepositoryRoot = root, TemporaryRoot = Path.Combine(root, "temporary"), ExpectedStoreId = "onboarding-test" };
        var store = new InternalGitRepositoryStore(Options.Create(options));
        using var archive = Archive(("README.md", "# Game"), ("assets/texture.bin", "\0binary"));
        var hash = Convert.ToHexString(SHA256.HashData(archive.ToArray())).ToLowerInvariant();
        var request = new InternalRepositoryImportRequest(Guid.NewGuid(), Guid.NewGuid(), hash);
        var first = await store.ImportAsync(request, archive, new(), default);
        archive.Position = 0; var replay = await store.ImportAsync(request, archive, new(), default);
        Assert.Equal(first.CommitSha, replay.CommitSha); Assert.True(replay.Resumed);
        var inspection = await store.ExecuteAsync(new(request.OrganizationId, request.RepositoryId, "inspect"));
        Assert.Single(inspection.Commits); Assert.Equal(first.CommitSha, inspection.Commits[0].Sha);
        using var different = Archive(("README.md", "replacement"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(request with { ArchiveSha256 = new string('b', 64) }, different, new(), default));
    }

    [Fact]
    public async Task BusinessHandoffResumesAfterLostImportResponseAndSavesCanonicalBrief()
    {
        await using var db = Database(); var user = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = user, DisplayName = "Owner" }); await db.SaveChangesAsync();
        var gitRoot = Path.Combine(root, "git"); Directory.CreateDirectory(gitRoot);
        File.WriteAllText(Path.Combine(gitRoot, ".csweet-git-store"), "handoff-test");
        var git = new InternalGitRepositoryStore(Options.Create(new InternalGitStorageOptions { RepositoryRoot = gitRoot, TemporaryRoot = Path.Combine(root, "git-temp"), ExpectedStoreId = "handoff-test" }));
        var host = new ImportHost(git); var store = Store(host); var audit = new NoAudit();
        var roles = new RoleService(db, audit);
        var businesses = new BusinessOnboardingService(new CoreOrganizationService(db, audit, roles), roles,
            new StrategicObjectiveService(db, audit), new WorkerService(db, audit), audit, new ExecutiveBriefingService(db, audit, TimeProvider.System), db);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var workflowLog = new RecordingWorkflowLog();
        var workflow = new RepositoryOnboardingWorkflow(db, store, host, null!, inbox, new Scheduler(), businesses,
            new ArtifactDocumentService(db, audit, TimeProvider.System), new InternalRepositoryManagementService(db, host, audit, TimeProvider.System), workflowLog);
        var service = new RepositoryOnboardingService(db, store, null!, host, null!, null!, inbox, workflow);
        var draft = await service.CreateAsync(user);
        using var zip = Archive(("game/README.md", "# Moss\nA cozy exploration game"));
        draft = await service.UploadAsync(user, draft.Id, draft.Revision, "game.zip", zip);
        var source = Assert.Single(draft.Sources);
        var review = new RepositoryBusinessReview("Moss Studio", "Games", "Create cozy worlds", "Ship a playable demo", "An exploration game studio",
            [new(source.Id.ToString(), "Moss", "A cozy exploration game", "Godot", "Prototype", [new(source.Id.ToString(), "README.md", source.Snapshot!)])], ["Who is the target audience?"]);
        draft = await service.SaveReviewAsync(user, draft.Id, new(draft.Revision, review));
        draft = await service.ConfirmAsync(user, draft.Id, new(draft.Revision, true));
        Assert.True(await service.ProcessNextAsync("test-worker"));
        var failed = await service.GetAsync(user, draft.Id);
        Assert.Equal("CreationFailed", failed.Status); Assert.NotNull(failed.OrganizationId);
        var repositoryId = Assert.Single(db.SourceControlRepositories).Id;
        await service.RetryAsync(user, draft.Id, failed.Revision); Assert.True(await service.ProcessNextAsync("test-worker"));
        var result = await service.GetAsync(user, draft.Id);
        Assert.True(result.Status == "Created", workflowLog.Error?.ToString()); Assert.NotNull(result.BriefArtifactId);
        Assert.Single(db.CoreOrganizations); Assert.Single(db.SourceControlRepositories); Assert.Equal(repositoryId, Assert.Single(db.SourceControlRepositories).Id);
        var artifact = Assert.Single(db.CoreArtifacts); Assert.Equal(result.BriefArtifactId, artifact.Id);
        Assert.Contains("Ship a playable demo", artifact.Content); Assert.Contains("README.md", artifact.Content);
        Assert.Empty(db.AgentInstallations); Assert.Empty(db.Workstreams); Assert.Empty(db.OrganizationTeams);
        Assert.Contains("Chief%20of%20Staff", result.ActionUri);
        Assert.Equal(result.OrganizationId, (await service.ConfirmAsync(user, draft.Id, new(result.Revision, true))).OrganizationId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(Guid.NewGuid(), draft.Id));
    }

    private static MemoryStream Archive(params (string Path, string Text)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Text); }
        stream.Position = 0; return stream;
    }
    private class SnapshotHost(byte[]? archive = null) : ITrustedSourceControlHostClient
    {
        public Task<TrustedWorkspaceSnapshot> PrepareOnboardingSnapshotAsync(TrustedWorkspaceSnapshotRequest r, CancellationToken t = default) =>
            Task.FromResult(new TrustedWorkspaceSnapshot("onboarding", new string('a', 40), false, archive!, "snapshot", 1, 1));
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest r, CancellationToken t = default) => throw new NotSupportedException();
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken t = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken t = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest r, CancellationToken t = default) => throw new NotSupportedException();
    }
    private sealed class ImportHost(InternalGitRepositoryStore store) : SnapshotHost, ITrustedSourceControlHostClient
    {
        private bool fail = true;
        public async Task<InternalRepositoryImportResult> ImportInternalArchiveAsync(InternalRepositoryImportRequest request, Stream archive, CancellationToken token = default)
        {
            var result = await store.ImportAsync(request, archive, new(), token);
            if (fail) { fail = false; throw new IOException("Simulated lost response after the import committed."); }
            return result;
        }
    }
    private sealed class NoAudit : IAuditEventWriter
    {
        public Task WriteAsync(string a, string b, Guid? c, string? d, string? e = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest r, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
    }
    private sealed class Scheduler : IPersonalAssistantRuntimeScheduler
    { public Guid Installation; public Task WakeAsync(Guid id, CancellationToken token) { Installation = id; return Task.CompletedTask; } }
    private sealed class RecordingWorkflowLog : Microsoft.Extensions.Logging.ILogger<RepositoryOnboardingWorkflow>
    {
        public Exception? Error;
        public IDisposable? BeginScope<T>(T state) where T : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<T>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, T state, Exception? exception, Func<T, Exception?, string> formatter) { if (exception is not null) Error = exception; }
    }
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
