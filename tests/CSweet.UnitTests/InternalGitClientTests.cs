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

    [Fact]
    public async Task NativeLfsLocksPersistAcrossTokensAndRequireExplicitForceForAnotherOwner()
    {
        await using var fixture = await Fixture.StartAsync();
        var first = await fixture.CreateAsync(new("First writer", true));
        var clone = Path.Combine(fixture.Root, "locks");
        await fixture.GitOkAsync(first.Token, "clone", fixture.Url, clone);
        await fixture.GitOkAsync(first.Token, "-C", clone, "lfs", "install", "--local");
        await fixture.GitOkAsync(first.Token, "-C", clone, "lfs", "lock", "art/texture.bin");
        Assert.NotEqual(0, (await fixture.GitAsync(first.Token, "-C", clone, "lfs", "lock", "art/texture.bin")).Code);
        var rotated = await fixture.CreateAsync(new("Rotated writer", true));
        var verification = await fixture.LockHttpAsync(rotated.Token, "POST", "locks/verify", "{}");
        Assert.Equal(200, verification.Code);
        using (var json = System.Text.Json.JsonDocument.Parse(verification.Body))
        { Assert.Single(json.RootElement.GetProperty("ours").EnumerateArray()); Assert.Empty(json.RootElement.GetProperty("theirs").EnumerateArray()); }
        var otherUser = Guid.NewGuid();
        await fixture.WithDbAsync(async db => { db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Business,
            ApplicationUserId = otherUser, DisplayName = "Other manager", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true }); await db.SaveChangesAsync(); });
        var other = await fixture.CreateAsync(new("Other writer", true), otherUser);
        Assert.NotEqual(0, (await fixture.GitAsync(other.Token, "-C", clone, "lfs", "unlock", "art/texture.bin")).Code);
        await fixture.GitOkAsync(other.Token, "-C", clone, "lfs", "unlock", "--force", "art/texture.bin");
        await fixture.GitOkAsync(rotated.Token, "-C", clone, "lfs", "lock", "art/texture.bin");
        await fixture.GitOkAsync(first.Token, "-C", clone, "lfs", "unlock", "art/texture.bin");
    }

    [Fact]
    public async Task LockApiPaginatesRejectsUnsafePathsAndRechecksRevokedAccess()
    {
        await using var fixture = await Fixture.StartAsync(); var writer = await fixture.CreateAsync(new("Writer", true));
        var read = await fixture.CreateAsync(new("Read only"));
        Assert.Equal(403, (await fixture.LockHttpAsync(read.Token, "POST", "locks", "{\"path\":\"asset.bin\"}")).Code);
        Assert.Equal(400, (await fixture.LockHttpAsync(writer.Token, "POST", "locks", "{\"path\":\"../asset.bin\"}")).Code);
        foreach (var path in new[] { "first.bin", "second.bin" })
            Assert.Equal(201, (await fixture.LockHttpAsync(writer.Token, "POST", "locks", System.Text.Json.JsonSerializer.Serialize(new { path }))).Code);
        var first = await fixture.LockHttpAsync(read.Token, "GET", "locks?limit=1");
        using var page = System.Text.Json.JsonDocument.Parse(first.Body);
        Assert.Single(page.RootElement.GetProperty("locks").EnumerateArray());
        var lockedId = page.RootElement.GetProperty("locks")[0].GetProperty("id").GetString();
        Assert.Equal(403, (await fixture.LockHttpAsync(read.Token, "POST", $"locks/{lockedId}/unlock", "{\"force\":true}")).Code);
        Assert.Equal(403, (await fixture.LockHttpAsync(read.Token, "POST", "locks/verify", "{}")).Code);
        var cursor = page.RootElement.GetProperty("next_cursor").GetString(); Assert.False(string.IsNullOrWhiteSpace(cursor));
        var second = await fixture.LockHttpAsync(read.Token, "GET", "locks?limit=1&cursor=" + cursor);
        using var next = System.Text.Json.JsonDocument.Parse(second.Body);
        Assert.Single(next.RootElement.GetProperty("locks").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, next.RootElement.GetProperty("next_cursor").ValueKind);
        await fixture.WithAccessAsync(s => s.RevokeAsync(fixture.Business, fixture.Repository, fixture.User, writer.Credential.Id, default));
        Assert.Equal(401, (await fixture.LockHttpAsync(writer.Token, "GET", "locks")).Code);
        Assert.Equal(200, (await fixture.LockHttpAsync(read.Token, "GET", "locks")).Code);
    }

    [Fact]
    public async Task NativeLfsVerificationBlocksAnotherUsersLockedAssetPush()
    {
        await using var fixture = await Fixture.StartAsync(); var owner = await fixture.CreateAsync(new("Owner", true));
        var clone = Path.Combine(fixture.Root, "locked-push");
        await fixture.GitOkAsync(owner.Token, "clone", fixture.Url, clone);
        await fixture.GitOkAsync(owner.Token, "-C", clone, "lfs", "install", "--local");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "checkout", "-b", "locked-assets");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "lfs", "track", "--lockable", "*.bin");
        await File.WriteAllTextAsync(Path.Combine(clone, "asset.bin"), "original asset");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "commit", "-m", "Original asset");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "push", "origin", "HEAD");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "lfs", "lock", "asset.bin");
        var user = Guid.NewGuid();
        await fixture.WithDbAsync(async db => { db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ApplicationUserId = user,
            DisplayName = "Contributor", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true }); await db.SaveChangesAsync(); });
        var contributor = await fixture.CreateAsync(new("Contributor", true), user);
        await File.WriteAllTextAsync(Path.Combine(clone, "asset.bin"), "competing asset");
        await fixture.GitOkAsync(contributor.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(contributor.Token, "-C", clone, "commit", "-m", "Competing asset");
        var push = await fixture.GitAsync(contributor.Token, "-C", clone, "-c", "lfs.locksverify=true", "push", "origin", "HEAD");
        Assert.NotEqual(0, push.Code); Assert.Contains("locked", push.Output, StringComparison.OrdinalIgnoreCase);
        var bypass = await fixture.GitAsync(contributor.Token, "-C", clone, "-c", "core.hooksPath=", "-c", "lfs.locksverify=false", "push", "origin", "HEAD");
        Assert.NotEqual(0, bypass.Code); Assert.Contains("locked file", bypass.Output, StringComparison.OrdinalIgnoreCase);
        var newBranchBypass = await fixture.GitAsync(contributor.Token, "-C", clone, "-c", "core.hooksPath=", "push", "origin", "HEAD:bypass-branch");
        Assert.NotEqual(0, newBranchBypass.Code); Assert.Contains("locked file", newBranchBypass.Output, StringComparison.OrdinalIgnoreCase);
        await fixture.GitOkAsync(owner.Token, "-C", clone, "-c", "lfs.locksverify=true", "push", "origin", "HEAD");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "lfs", "unlock", "asset.bin");
        await fixture.GitOkAsync(contributor.Token, "-C", clone, "-c", "lfs.locksverify=true", "push", "origin", "HEAD");
    }

    [Fact]
    public async Task LockManagementRequiresManagerAndArchivedRepositoriesRemainInspectable()
    {
        await using var fixture = await Fixture.StartAsync();
        var viewer = Guid.NewGuid();
        await fixture.WithDbAsync(async db => { db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ApplicationUserId = viewer,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Viewer, IsActive = true }); await db.SaveChangesAsync(); });
        await fixture.WithAccessAsync(async service =>
        {
            var created = await service.ManageLocksAsync(fixture.Business, fixture.Repository, fixture.User, new("create", Path: "art.bin"), default);
            Assert.Equal(201, created.StatusCode);
            Assert.Single((await service.ManageLocksAsync(fixture.Business, fixture.Repository, viewer, new("list"), default)).Locks);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ManageLocksAsync(fixture.Business, fixture.Repository, viewer, new("unlock", Id: created.Locks[0].Id, Force: true), default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ManageLocksAsync(Guid.NewGuid(), fixture.Repository, fixture.User, new("list"), default));
        });
        await fixture.WithDbAsync(async db => { var repo = await db.SourceControlRepositories.SingleAsync(); repo.Status = SourceControlRepositoryStatus.Archived; repo.ArchivedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); });
        await fixture.WithAccessAsync(async service =>
        {
            Assert.Single((await service.ManageLocksAsync(fixture.Business, fixture.Repository, fixture.User, new("list"), default)).Locks);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ManageLocksAsync(fixture.Business, fixture.Repository, fixture.User, new("create", Path: "other.bin"), default));
        });
    }

    [Fact]
    public async Task ServerLocksUseLiteralPathsAndRejectRenameAndDeleteWithoutClientHooks()
    {
        await using var fixture = await Fixture.StartAsync(); var owner = await fixture.CreateAsync(new("Owner", true, true));
        var clone = Path.Combine(fixture.Root, "literal-locks"); await fixture.GitOkAsync(owner.Token, "clone", fixture.Url, clone);
        const string lockedPath = "asset [d] $.bin";
        await File.WriteAllTextAsync(Path.Combine(clone, lockedPath), "original");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "commit", "-m", "Original");
        await fixture.GitOkAsync(owner.Token, "-C", clone, "push", "origin", "main");
        Assert.Equal(201, (await fixture.LockHttpAsync(owner.Token, "POST", "locks", System.Text.Json.JsonSerializer.Serialize(new { path = lockedPath }))).Code);
        var user = Guid.NewGuid();
        await fixture.WithDbAsync(async db => { db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Business, ApplicationUserId = user,
            DisplayName = "Other", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true }); await db.SaveChangesAsync(); });
        var other = await fixture.CreateAsync(new("Other", true, true), user);
        await File.WriteAllTextAsync(Path.Combine(clone, "asset d $.bin"), "unrelated");
        await fixture.GitOkAsync(other.Token, "-C", clone, "add", ".");
        await fixture.GitOkAsync(other.Token, "-C", clone, "commit", "-m", "Unrelated");
        await fixture.GitOkAsync(other.Token, "-C", clone, "-c", "core.hooksPath=", "push", "origin", "main");
        await fixture.GitOkAsync(other.Token, "-C", clone, "mv", "--", lockedPath, "renamed.bin");
        await fixture.GitOkAsync(other.Token, "-C", clone, "commit", "-m", "Rename locked file");
        var rename = await fixture.GitAsync(other.Token, "-C", clone, "-c", "core.hooksPath=", "push", "origin", "main");
        Assert.NotEqual(0, rename.Code); Assert.Contains("locked file", rename.Output);
        await fixture.GitOkAsync(other.Token, "-C", clone, "rm", "renamed.bin");
        await fixture.GitOkAsync(other.Token, "-C", clone, "commit", "-m", "Delete locked file");
        var deletion = await fixture.GitAsync(other.Token, "-C", clone, "-c", "core.hooksPath=", "push", "origin", "main");
        Assert.NotEqual(0, deletion.Code); Assert.Contains("locked file", deletion.Output);
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
        public async Task<CreatedInternalGitAccess> CreateAsync(CreateInternalGitAccessRequest request, Guid? user = null)
        { using var scope = _app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<InternalGitAccessService>().CreateAsync(Business, Repository, user ?? User, request, default); }
        public async Task WithAccessAsync(Func<InternalGitAccessService, Task> action)
        { using var scope = _app.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<InternalGitAccessService>()); }
        public async Task WithDbAsync(Func<CSweetDbContext, Task> action)
        { using var scope = _app.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<CSweetDbContext>()); }
        public async Task<(int Code, string Body)> LockHttpAsync(string token, string method, string path, string? body = null)
        {
            using var client = new HttpClient(); using var request = new HttpRequestMessage(new HttpMethod(method), Url + "/info/lfs/" + path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("csweet:" + token)));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/vnd.git-lfs+json");
            using var response = await client.SendAsync(request); return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
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
        public Task<InternalGitLockResult> InternalLocksAsync(InternalGitLockRequest request, CancellationToken ct = default) => store.LocksAsync(request, ct);
        public Task<InternalGitLfsTransferResult> TransferInternalLfsAsync(InternalGitLfsTransfer request, CancellationToken ct = default) => store.TransferLfsAsync(request, ct);
        public Task<InternalGitHttpResponse> ExchangeInternalGitAsync(InternalGitHttpRequest request, CancellationToken ct = default) => store.ExchangeAsync(request, ct);
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
