using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Api.SourceControl;
using CSweet.Application.Security;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class AgentWorkspaceBrokerTests
{
    [Fact]
    public async Task NativeGitCanCloneAndPushThroughHeadquartersAndReadHookRejections()
    {
        await using var fixture = await GitEntryFixture.StartAsync(nativeGit: true);
        var clone = System.IO.Path.Combine(fixture.NativeRoot!, "clone");
        var url = new Uri(fixture.Client.BaseAddress!, fixture.Path.TrimEnd('/')).AbsoluteUri;
        Assert.Equal(0, (await fixture.GitAsync("clone", url, clone)).Code);
        await File.WriteAllTextAsync(System.IO.Path.Combine(clone, "hello.txt"), "hello");
        Assert.Equal(0, (await fixture.GitAsync("-C", clone, "add", ".")).Code);
        Assert.Equal(0, (await fixture.GitAsync("-C", clone, "commit", "-m", "Agent commit")).Code);
        var push = await fixture.GitAsync("-C", clone, "push", "origin", "HEAD:" + fixture.Workspace.BranchName);
        Assert.True(push.Code == 0, push.Output);
        var rejected = await fixture.GitAsync("-C", clone, "push", "origin", "HEAD:main");
        Assert.NotEqual(0, rejected.Code);
        Assert.Contains("only to the assigned work branch", rejected.Output);
        Assert.Empty(await fixture.Db.SourceControlPublications.ToArrayAsync());
    }

    [Theory]
    [InlineData("git-upload-pack", true)]
    [InlineData("git-upload-pack", false)]
    [InlineData("git-receive-pack", true)]
    [InlineData("git-receive-pack", false)]
    public async Task GitEntryPointUsesSessionAndPersistedWorkspaceAcrossCore(string service, bool advertise)
    {
        await using var fixture = await GitEntryFixture.StartAsync();
        using var response = await fixture.SendAsync(service, advertise, gzip: !advertise);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(fixture.Session.Id.ToString(), Assert.Single(response.Headers.GetValues("Mcp-Session-Id")));
        Assert.Equal($"application/x-{service}-result", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 9 }, await response.Content.ReadAsByteArrayAsync());
        var git = fixture.Host.GitRequest!;
        Assert.Equal(fixture.Workspace.RepositoryId, git.RepositoryId);
        Assert.Equal(fixture.Workspace.OrganizationId, git.OrganizationId);
        Assert.Equal(fixture.Workspace.BranchName, git.AgentBranch);
        Assert.Equal(fixture.Workspace.BaseCommitSha, git.AgentBaseCommitSha);
        Assert.Equal(advertise ? [] : new byte[] { 0, 255, 1, 13, 10 }, git.Body);
        Assert.Equal(service == "git-receive-pack" ? GitWorkspaceCapabilities.Publish : GitWorkspaceCapabilities.Prepare,
            Assert.Single(fixture.Authorization.Actions));
        Assert.Empty(await fixture.Db.SourceControlPublications.ToArrayAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-token")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("changed-grant")]
    [InlineData("stopped")]
    public async Task GitEntryPointRejectsUnauthenticatedRuntimeBeforeHostAccess(string scenario)
    {
        await using var fixture = await GitEntryFixture.StartAsync();
        if (scenario == "missing") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (scenario == "wrong-token") fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");
        if (scenario == "revoked") fixture.Session.RevokedAt = DateTimeOffset.UtcNow;
        if (scenario == "expired") fixture.Session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (scenario == "changed-grant") fixture.Installation.Grant!.GrantRevision++;
        if (scenario == "stopped") fixture.Runtime.TransitionTo(AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
        await fixture.Db.SaveChangesAsync();
        using var response = await fixture.SendAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.Host.GitRequest);
        Assert.Empty(fixture.Authorization.Actions);
    }

    [Theory]
    [InlineData("foreign-workspace", HttpStatusCode.NotFound)]
    [InlineData("stale-revision", HttpStatusCode.Forbidden)]
    [InlineData("missing-capability", HttpStatusCode.Forbidden)]
    [InlineData("scope-denied", HttpStatusCode.Forbidden)]
    [InlineData("membership-revoked", HttpStatusCode.Forbidden)]
    [InlineData("core-revocation", HttpStatusCode.Forbidden)]
    [InlineData("fenced", HttpStatusCode.Forbidden)]
    public async Task GitEntryPointRejectsInvalidPushAuthority(string scenario, HttpStatusCode expected)
    {
        await using var fixture = await GitEntryFixture.StartAsync();
        if (scenario == "foreign-workspace") fixture.Workspace.AgentInstallationId = Guid.NewGuid();
        if (scenario == "stale-revision") fixture.Workspace.AssignmentRevision++;
        if (scenario == "missing-capability") fixture.Installation.Grant!.RequiredCapabilitiesJson = "[]";
        if (scenario == "scope-denied") fixture.Authorization.Allow = false;
        if (scenario == "membership-revoked") (await fixture.Db.TeamMemberships.SingleAsync()).EndedAt = DateTimeOffset.UtcNow;
        if (scenario == "core-revocation") fixture.Authorization.OnAuthorize = async () =>
        {
            // Change authority after AgentHost's read, proving that Core independently rechecks it.
            (await fixture.Db.TeamRepositoryPolicies.SingleAsync()).DisabledAt = DateTimeOffset.UtcNow;
            await fixture.Db.SaveChangesAsync();
        };
        if (scenario == "fenced") fixture.Runtime.MemoryResetRequestedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();
        using var response = await fixture.SendAsync("git-receive-pack", false);
        Assert.Equal(expected, response.StatusCode);
        Assert.Null(fixture.Host.GitRequest);
    }

    [Theory]
    [InlineData("info/refs?service=git-upload-archive", "GET", null, HttpStatusCode.NotFound)]
    [InlineData("git-upload-pack", "GET", null, HttpStatusCode.NotFound)]
    [InlineData("git-receive-pack", "POST", "application/json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("git-upload-pack?repositoryId=forged", "POST", "application/x-git-upload-pack-request", HttpStatusCode.BadRequest)]
    [InlineData("info/refs?service=git-upload-pack&workspaceKey=forged", "GET", null, HttpStatusCode.BadRequest)]
    public async Task GitEntryPointRejectsUnsupportedProtocolShapes(string path, string method, string? contentType, HttpStatusCode expected)
    {
        await using var fixture = await GitEntryFixture.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), fixture.Path + path);
        if (contentType is not null)
        {
            request.Content = new ByteArrayContent([1]);
            request.Content.Headers.ContentType = new(contentType);
        }
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Null(fixture.Host.GitRequest);
    }

    [Fact]
    public async Task GitEntryPointBoundsDecompressedBodyAndRejectsAdvertisementBodies()
    {
        await using var fixture = await GitEntryFixture.StartAsync();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(new byte[AgentWorkspaceGitEndpoints.MaximumBodyBytes + 1]);
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Path + "git-receive-pack")
            { Content = new ByteArrayContent(compressed.ToArray()) };
        request.Content.Headers.ContentType = new("application/x-git-receive-pack-request");
        request.Content.Headers.ContentEncoding.Add("gzip");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var advertisement = new HttpRequestMessage(HttpMethod.Get, fixture.Path + "info/refs?service=git-upload-pack")
            { Content = new ByteArrayContent([1]) };
        using var invalid = await fixture.Client.SendAsync(advertisement);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null(fixture.Host.GitRequest);
    }

    [Fact]
    public async Task CoreGitEndpointRequiresServiceAuthentication()
    {
        await using var fixture = await GitEntryFixture.StartAsync();
        using var client = new HttpClient { BaseAddress = fixture.CoreUri };
        using var response = await client.PostAsJsonAsync("agent-broker/v2/workspaces/git",
            new AgentBrokerWorkspaceGitRequest(fixture.Request, "git-upload-pack", true, []));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.Host.GitRequest);
    }

    private sealed class GitEntryFixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; private set; } = null!;
        public FakeHost Host { get; } = new();
        public GitEntryAuthorization Authorization { get; } = new();
        public SourceControlWorkspace Workspace { get; private set; } = null!;
        public AgentInstallation Installation { get; private set; } = null!;
        public AgentRuntimeInstance Runtime { get; private set; } = null!;
        public McpAgentSession Session { get; private set; } = null!;
        public AgentBrokerWorkspaceOperationRequest Request { get; private set; } = null!;
        private WebApplication core = null!, agentHost = null!;
        private HttpClient coreClient = null!;
        public HttpClient Client { get; private set; } = null!;
        public string? NativeRoot { get; private set; }
        public Uri CoreUri => new(core.Urls.Single());
        public string Path => $"/git/workspaces/{Workspace.Id:D}/1/";

        public static async Task<GitEntryFixture> StartAsync(bool nativeGit = false)
        {
            var fixture = new GitEntryFixture();
            fixture.Db = new(new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            var db = fixture.Db;
            fixture.Request = await SeedOperationAsync(db, "snapshot-pull");
            fixture.Workspace = await db.SourceControlWorkspaces.SingleAsync();
            fixture.Installation = await db.AgentInstallations.SingleAsync();
            var workspace = fixture.Workspace;
            if (nativeGit)
            {
                fixture.NativeRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csweet-git-entry-tests", Guid.NewGuid().ToString("N"));
                var repositories = System.IO.Path.Combine(fixture.NativeRoot, "repositories");
                Directory.CreateDirectory(repositories);
                await File.WriteAllTextAsync(System.IO.Path.Combine(repositories, ".csweet-git-store"), "entry-test");
                fixture.Host.NativeStore = new InternalGitRepositoryStore(Options.Create(new InternalGitStorageOptions
                    { RepositoryRoot = repositories, ExpectedStoreId = "entry-test", TemporaryRoot = System.IO.Path.Combine(fixture.NativeRoot, "temp") }));
                await fixture.Host.NativeStore.ExecuteAsync(new(workspace.OrganizationId, workspace.RepositoryId, "create", "main"));
                var prepared = await fixture.Host.NativeStore.PrepareAsync(new(workspace.OrganizationId, workspace.RepositoryId,
                    workspace.Id, "main", workspace.BranchName, null, "prepare"), new WorkspaceArtifactValidator());
                workspace.BaseCommitSha = prepared.BaseCommitSha;
            }
            var task = await db.CoreWorkTasks.SingleAsync();
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = workspace.OrganizationId, TeamId = workspace.TeamId };
            task.BoardId = board.Id;
            task.DevelopmentBriefJson = JsonSerializer.Serialize(new SoftwareDevelopmentBrief(workspace.RepositoryId, "polyglot", [], []));
            var package = new AgentPackageVersion { Id = fixture.Installation.PackageVersionId, AgentId = "developer", ManifestDigest = "digest" };
            var grant = new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = fixture.Installation.Id,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Publish }) };
            fixture.Installation.PackageVersion = package;
            fixture.Installation.Grant = grant;
            fixture.Runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
                AgentInstallationId = fixture.Installation.Id, RuntimeDeadlineAt = DateTimeOffset.UtcNow.AddHours(1) };
            fixture.Runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
            fixture.Runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
            fixture.Runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
            fixture.Session = new McpAgentSession { Id = Guid.NewGuid(), RuntimeInstanceId = fixture.Runtime.Id,
                TickId = fixture.Runtime.TickId, AgentInstallationId = fixture.Installation.Id, OrganizationId = fixture.Installation.BusinessId,
                PackageVersionId = package.Id, PackageDigest = package.ManifestDigest, GrantRevision = 1,
                AccessTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("git-session-token"))),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
            db.AddRange(board, package, grant, fixture.Runtime, fixture.Session);
            await db.SaveChangesAsync();

            var auth = Options.Create(new AgentBrokerAuthenticationOptions { SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
            var builder = Builder();
            builder.Services.AddSingleton<IAgentWorkspaceBroker>(new AgentWorkspaceBroker(db, fixture.Host, new FakeVolumes(), WorkspaceSyncTestOptions.Value));
            builder.Services.AddSingleton(new PersonalRepositoryBroker(db, fixture.Host));
            builder.Services.AddSingleton(auth);
            builder.Services.AddSingleton<TrustedRequestReplayCache>();
            builder.Services.AddSingleton(TimeProvider.System);
            fixture.core = builder.Build();
            fixture.core.UseAgentBrokerAuthentication();
            fixture.core.MapAgentWorkspaceBrokerEndpoints();
            await fixture.core.StartAsync();
            fixture.coreClient = new HttpClient(new AgentBrokerAuthenticationHandler(auth, TimeProvider.System)
                { InnerHandler = new HttpClientHandler() }) { BaseAddress = fixture.CoreUri };

            builder = Builder();
            builder.Services.AddSingleton(new McpAgentSessionService(db, null!, null!, null!, null!, TimeProvider.System));
            builder.Services.AddSingleton(new GitWorkspaceCapabilityHandler(db, new CoreWorkspaceBrokerClient(fixture.coreClient),
                fixture.Authorization, null!, WorkspaceSyncTestOptions.Value));
            builder.Services.AddRateLimiter(options => options.AddPolicy("mcp-session", new McpIngressPolicy()));
            fixture.agentHost = builder.Build();
            fixture.agentHost.Use(async (context, next) =>
            {
                await McpIngressPolicy.AuthenticateAsync(context, context.RequestServices.GetRequiredService<McpAgentSessionService>());
                await next(context);
            });
            fixture.agentHost.UseRateLimiter();
            fixture.agentHost.MapAgentWorkspaceGitEndpoints();
            await fixture.agentHost.StartAsync();
            fixture.Client = new HttpClient { BaseAddress = new(fixture.agentHost.Urls.Single()) };
            fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", "git-session-token");
            fixture.Client.DefaultRequestHeaders.Add("Mcp-Session-Id", fixture.Session.Id.ToString());
            return fixture;
        }

        public async Task<HttpResponseMessage> SendAsync(string service = "git-upload-pack", bool advertise = true, bool gzip = false)
        {
            using var request = new HttpRequestMessage(advertise ? HttpMethod.Get : HttpMethod.Post,
                Path + (advertise ? "info/refs?service=" + service : service));
            if (!advertise)
            {
                byte[] bytes = [0, 255, 1, 13, 10];
                if (gzip)
                {
                    using var compressed = new MemoryStream();
                    using (var zip = new GZipStream(compressed, CompressionLevel.Fastest, true)) zip.Write(bytes);
                    bytes = compressed.ToArray();
                }
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new($"application/x-{service}-request");
                if (gzip) request.Content.Headers.ContentEncoding.Add("gzip");
            }
            return await Client.SendAsync(request);
        }

        private static WebApplicationBuilder Builder()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = System.IO.Path.GetTempPath(), Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            return builder;
        }

        public async Task<(int Code, string Output)> GitAsync(params string[] args)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            foreach (var config in new[] { "user.name=Test", "user.email=test@localhost", "credential.helper=", "core.autocrlf=false",
                         "http.extraHeader=Authorization: Bearer git-session-token", "http.extraHeader=Mcp-Session-Id: " + Session.Id })
            { start.ArgumentList.Add("-c"); start.ArgumentList.Add(config); }
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await agentHost.DisposeAsync();
            coreClient.Dispose();
            await core.DisposeAsync();
            await Db.DisposeAsync();
            if (NativeRoot is not null)
            {
                var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csweet-git-entry-tests")) + System.IO.Path.DirectorySeparatorChar;
                if (!System.IO.Path.GetFullPath(NativeRoot).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid test cleanup path.");
                foreach (var file in Directory.EnumerateFiles(NativeRoot, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(NativeRoot, true);
            }
        }
    }

    private sealed class GitEntryAuthorization : IScopedActionAuthorizationService
    {
        public bool Allow { get; set; } = true;
        public List<string> Actions { get; } = [];
        public Func<Task>? OnAuthorize { get; set; }
        public async Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid organizationId, GrantSubjectKind subjectKind,
            Guid subjectId, string action, GrantScopeKind resourceScopeKind, Guid? resourceScopeId, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            if (OnAuthorize is not null) await OnAuthorize();
            return new(Allow, action);
        }
    }
}
