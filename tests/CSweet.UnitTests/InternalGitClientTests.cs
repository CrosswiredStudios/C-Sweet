using System.Diagnostics;
using System.Text;
using CSweet.Api.SourceControl;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InternalGitClientTests
{
    [Fact]
    public async Task NativeGitClonesPushesBranchAndPullsWhileProtectingDefaultAndRevokingAccess()
    {
        await using var fixture = await Fixture.StartAsync();
        var credential = await fixture.CreateAsync(new("Laptop", true));
        var clone = Path.Combine(fixture.Root, "clone");
        await fixture.GitOkAsync(credential.Token, "clone", fixture.Url, clone);
        await fixture.GitOkAsync(credential.Token, "-C", clone, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(clone, "hello.txt"), "hello git");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "commit", "-m", "Add file");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "push", "origin", "HEAD");
        var rejected = await fixture.GitAsync(credential.Token, "-C", clone, "push", "origin", "HEAD:main");
        Assert.NotEqual(0, rejected.Code); Assert.Contains("protects this branch", rejected.Output);
        await fixture.GitOkAsync(credential.Token, "-C", clone, "pull", "--ff-only", "origin", "feature");
        Assert.NotEqual(0, (await fixture.GitAsync(credential.Token, "-C", clone, "push", "origin", "HEAD:refs/csweet/forged")).Code);
        var second = Path.Combine(fixture.Root, "second");
        await fixture.GitOkAsync(credential.Token, "clone", "--branch", "feature", fixture.Url, second);
        Assert.Equal("hello git", await File.ReadAllTextAsync(Path.Combine(second, "hello.txt")));
        await fixture.WithAccessAsync(s => s.RevokeAsync(fixture.Business, fixture.Repository, fixture.User, credential.Credential.Id, default));
        Assert.NotEqual(0, (await fixture.GitAsync(credential.Token, "ls-remote", fixture.Url)).Code);
    }

    [Fact]
    public async Task ReadOnlyCredentialCannotPushAndManagerCanExplicitlyAllowDefaultBranch()
    {
        await using var fixture = await Fixture.StartAsync();
        var read = await fixture.CreateAsync(new("Read only"));
        var clone = Path.Combine(fixture.Root, "clone");
        await fixture.GitOkAsync(read.Token, "clone", fixture.Url, clone);
        await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), "first version");
        await fixture.GitOkAsync(read.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(read.Token, "-C", clone, "commit", "-m", "Initial content");
        Assert.NotEqual(0, (await fixture.GitAsync(read.Token, "-C", clone, "push", "origin", "HEAD:feature")).Code);
        var write = await fixture.CreateAsync(new("Administrator", true, true));
        await fixture.GitOkAsync(write.Token, "-C", clone, "push", "origin", "main");
        await fixture.WithDbAsync(async db =>
        {
            db.SourceControlWorkspaces.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Business, RepositoryId = fixture.Repository,
                BranchName = "agent-work", Status = SourceControlWorkspaceStatus.Ready });
            await db.SaveChangesAsync();
        });
        Assert.NotEqual(0, (await fixture.GitAsync(write.Token, "-C", clone, "push", "origin", "HEAD:agent-work")).Code);
    }

    [Fact]
    public async Task CredentialsAreHashedScopedAndRecheckMembership()
    {
        await using var fixture = await Fixture.StartAsync();
        var credential = await fixture.CreateAsync(new("Private token", true));
        await fixture.WithDbAsync(async db => Assert.DoesNotContain(credential.Token, (await db.SourceControlCredentials.SingleAsync()).ProtectedPayload));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.WithAccessAsync(s => s.AuthorizeAsync(fixture.Business, Guid.NewGuid(), credential.Token, "git-upload-pack", default)));
        await fixture.WithDbAsync(async db => { (await db.CoreOrganizationUsers.SingleAsync()).PermissionLevel = OrganizationPermissionLevel.Viewer; await db.SaveChangesAsync(); });
        await fixture.WithAccessAsync(s => s.AuthorizeAsync(fixture.Business, fixture.Repository, credential.Token, "git-upload-pack", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.WithAccessAsync(s => s.AuthorizeAsync(fixture.Business, fixture.Repository, credential.Token, "git-receive-pack", default)));
        await fixture.WithDbAsync(async db => { (await db.CoreOrganizationUsers.SingleAsync()).IsActive = false; await db.SaveChangesAsync(); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.WithAccessAsync(s => s.AuthorizeAsync(fixture.Business, fixture.Repository, credential.Token, "git-upload-pack", default)));
    }

    [Fact]
    public async Task ExpiredCredentialIsRejected()
    {
        await using var fixture = await Fixture.StartAsync(); var credential = await fixture.CreateAsync(new("Expired"));
        await fixture.WithDbAsync(async db =>
        {
            var row = await db.SourceControlCredentials.SingleAsync();
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(row.ProtectedPayload)!;
            metadata["ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            row.ProtectedPayload = metadata.ToJsonString(); await db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.WithAccessAsync(s => s.AuthorizeAsync(fixture.Business, fixture.Repository, credential.Token, "git-upload-pack", default)));
    }

    [Fact]
    public async Task NativeLfsUploadsAssetAndDownloadsIntoAnotherClone()
    {
        await using var fixture = await Fixture.StartAsync(); var credential = await fixture.CreateAsync(new("Assets", true));
        var clone = Path.Combine(fixture.Root, "assets");
        await fixture.GitOkAsync(credential.Token, "clone", fixture.Url, clone);
        await fixture.GitOkAsync(credential.Token, "-C", clone, "lfs", "install", "--local");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "checkout", "-b", "assets");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "lfs", "track", "*.bin");
        var bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(clone, "texture.bin"), bytes);
        await fixture.GitOkAsync(credential.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "commit", "-m", "Add LFS asset");
        await fixture.GitOkAsync(credential.Token, "-C", clone, "push", "origin", "HEAD");
        var second = Path.Combine(fixture.Root, "asset-download");
        await fixture.GitOkAsync(credential.Token, "clone", "--branch", "assets", fixture.Url, second);
        await fixture.GitOkAsync(credential.Token, "-C", second, "lfs", "install", "--local");
        await fixture.GitOkAsync(credential.Token, "-C", second, "lfs", "pull");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(second, "texture.bin")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "csweet-client-tests", Guid.NewGuid().ToString("N"));
        public Guid Business { get; } = Guid.NewGuid();
        public Guid Repository { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        private WebApplication _app = null!;
        public string Url => $"{_app.Urls.Single()}/git/{Business:D}/{Repository:D}.git";
        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            var repositories = Path.Combine(fixture.Root, "repositories"); Directory.CreateDirectory(repositories);
            await File.WriteAllTextAsync(Path.Combine(repositories, ".csweet-git-store"), "test");
            var store = new InternalGitRepositoryStore(Options.Create(new InternalGitStorageOptions { RepositoryRoot = repositories, ExpectedStoreId = "test", TemporaryRoot = Path.Combine(fixture.Root, "temp") }));
            await store.ExecuteAsync(new(fixture.Business, fixture.Repository, "create", "main"));
            await store.PrepareAsync(new(fixture.Business, fixture.Repository, Guid.NewGuid(), "main", "feature", null, "prepare"), new WorkspaceArtifactValidator());
            var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var database = Guid.NewGuid().ToString();
            builder.Services.AddDbContext<CSweetDbContext>(o => o.UseInMemoryDatabase(database));
            builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddSingleton<IAuditEventWriter, Audit>();
            builder.Services.AddSingleton<ITrustedSourceControlHostClient>(new Host(store)); builder.Services.AddScoped<InternalGitAccessService>();
            fixture._app = builder.Build(); fixture._app.MapInternalGitHttpEndpoints();
            await fixture.WithDbAsync(async db =>
            {
                var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = fixture.Business, Provider = SourceControlProvider.InternalGit, Status = SourceControlConnectionStatus.Connected };
                db.AddRange(connection, new SourceControlRepository { Id = fixture.Repository, OrganizationId = fixture.Business, ConnectionId = connection.Id, IsPrivate = true, DefaultBranch = "main", Status = SourceControlRepositoryStatus.Ready },
                    new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ApplicationUserId = fixture.User, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true });
                await db.SaveChangesAsync();
            });
            await fixture._app.StartAsync(); return fixture;
        }
        public async Task<CreatedInternalGitAccess> CreateAsync(CreateInternalGitAccessRequest request)
        { using var scope = _app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<InternalGitAccessService>().CreateAsync(Business, Repository, User, request, default); }
        public async Task WithAccessAsync(Func<InternalGitAccessService, Task> action)
        { using var scope = _app.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<InternalGitAccessService>()); }
        public async Task WithDbAsync(Func<CSweetDbContext, Task> action)
        { using var scope = _app.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<CSweetDbContext>()); }
        public async Task GitOkAsync(string token, params string[] args)
        { var result = await GitAsync(token, args); Assert.True(result.Code == 0, result.Output); }
        public async Task<(int Code, string Output)> GitAsync(string token, params string[] args)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            start.Environment["GIT_CONFIG_COUNT"] = "1"; start.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            start.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("csweet:" + token));
            foreach (var config in new[] { "user.name=Test", "user.email=test@localhost", "credential.helper=", "core.longpaths=true", "lfs.transfer.maxretries=1" }) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(config); }
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(); await _app.DisposeAsync();
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-client-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path.");
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string type, string entity, Guid? id, string? summary, string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
    }
    private sealed class Host(InternalGitRepositoryStore store) : ITrustedSourceControlHostClient
    {
        public Task<InternalGitLfsTransferResult> TransferInternalLfsAsync(InternalGitLfsTransfer request, CancellationToken ct = default) => store.TransferLfsAsync(request, ct);
        public Task<InternalGitHttpResponse> ExchangeInternalGitAsync(InternalGitHttpRequest request, CancellationToken ct = default) => store.ExchangeAsync(request, ct);
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
