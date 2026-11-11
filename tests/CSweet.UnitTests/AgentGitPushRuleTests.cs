using System.Diagnostics;
using System.IO.Compression;
using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

/// <summary>Drives the GitHost agent push rules with a native Git client over smart HTTP.</summary>
public sealed class AgentGitPushRuleTests
{
    [Fact]
    public async Task AgentPushesOnlyItsOwnBranchWithoutRewritesLinksOrLargeFiles()
    {
        await using var fixture = await Fixture.StartAsync();
        var clone = Path.Combine(fixture.Root, "clone");
        var target = "work:" + Fixture.Branch;
        await fixture.GitOkAsync("clone", fixture.Url, clone);
        await fixture.GitOkAsync("-C", clone, "checkout", "-b", "work");
        await fixture.CommitFileAsync(clone, "one.txt", "one");
        await fixture.GitOkAsync("-C", clone, "push", "origin", target);
        await fixture.CommitFileAsync(clone, "two words.txt", "two");
        await fixture.GitOkAsync("-C", clone, "push", "origin", target);

        await fixture.RejectedAsync("only to the assigned work branch", "-C", clone, "push", "origin", "work:other");
        await fixture.RejectedAsync("only to the assigned work branch", "-C", clone, "push", "origin", "work:main");
        await fixture.GitOkAsync("-C", clone, "tag", "v1");
        await fixture.RejectedAsync("only to the assigned work branch", "-C", clone, "push", "origin", "v1");
        await fixture.RejectedAsync("does not let agents delete", "-C", clone, "push", "origin", ":" + Fixture.Branch);

        // Index entries stand in for a link and a submodule, so the test needs no symlink privilege.
        var blob = (await fixture.GitAsync("-C", clone, "rev-parse", "HEAD:one.txt")).Output.Trim();
        await fixture.GitOkAsync("-C", clone, "update-index", "--add", "--cacheinfo", "120000," + blob + ",link");
        await fixture.GitOkAsync("-C", clone, "commit", "-m", "Add link");
        await fixture.RejectedAsync("symbolic links and submodules", "-C", clone, "push", "origin", target);
        await fixture.GitOkAsync("-C", clone, "reset", "--hard", "HEAD~1");
        var commit = (await fixture.GitAsync("-C", clone, "rev-parse", "HEAD")).Output.Trim();
        await fixture.GitOkAsync("-C", clone, "update-index", "--add", "--cacheinfo", "160000," + commit + ",module");
        await fixture.GitOkAsync("-C", clone, "commit", "-m", "Add submodule");
        await fixture.RejectedAsync("symbolic links and submodules", "-C", clone, "push", "origin", target);
        await fixture.GitOkAsync("-C", clone, "reset", "--hard", "HEAD~1");

        await File.WriteAllBytesAsync(Path.Combine(clone, "big file.bin"), new byte[Fixture.MaximumBlobBytes + 1]);
        await fixture.GitOkAsync("-C", clone, "add", ".");
        await fixture.GitOkAsync("-C", clone, "commit", "-m", "Add large file");
        await fixture.RejectedAsync("big file.bin", "-C", clone, "push", "origin", target);
        await fixture.GitOkAsync("-C", clone, "rm", "big file.bin");
        await fixture.GitOkAsync("-C", clone, "commit", "-m", "Remove large file");
        // The large object is still part of the history being pushed.
        await fixture.RejectedAsync("big file.bin", "-C", clone, "push", "origin", target);
        await fixture.GitOkAsync("-C", clone, "reset", "--hard", "HEAD~2");
        await File.WriteAllBytesAsync(Path.Combine(clone, "edge.bin"), new byte[Fixture.MaximumBlobBytes]);
        await fixture.GitOkAsync("-C", clone, "add", ".");
        await fixture.GitOkAsync("-C", clone, "commit", "-m", "Add file at the limit");
        await fixture.GitOkAsync("-C", clone, "push", "origin", target);

        await fixture.GitOkAsync("-C", clone, "commit", "--amend", "-m", "Rewritten");
        Assert.NotEqual(0, (await fixture.GitAsync("-C", clone, "push", "--force", "origin", target)).Code);
    }

    [Fact]
    public async Task AgentBranchMustDescendFromItsAssignedBaseCommit()
    {
        await using var fixture = await Fixture.StartAsync();
        var clone = Path.Combine(fixture.Root, "clone");
        await fixture.GitOkAsync("clone", fixture.Url, clone);
        await fixture.GitOkAsync("-C", clone, "checkout", "--orphan", "lone");
        await fixture.CommitFileAsync(clone, "lone.txt", "lone");
        await fixture.RejectedAsync("descend from its assigned base commit", "-C", clone, "push", "origin", "lone:" + Fixture.Branch);
        await fixture.GitOkAsync("-C", clone, "checkout", "-b", "work", "origin/main");
        await fixture.CommitFileAsync(clone, "work.txt", "work");
        await fixture.GitOkAsync("-C", clone, "push", "origin", "work:" + Fixture.Branch);
    }

    [Fact]
    public async Task PushedCommitsPublishWithoutANewCommitAndOnlyAtTheNamedTip()
    {
        await using var fixture = await Fixture.StartAsync();
        var clone = Path.Combine(fixture.Root, "clone");
        var target = "work:" + Fixture.Branch;
        await fixture.GitOkAsync("clone", fixture.Url, clone);
        await fixture.GitOkAsync("-C", clone, "checkout", "-b", "work");
        var request = new InternalGitPushedPublication(fixture.Business, fixture.Repository, Guid.NewGuid(),
            fixture.Base, Fixture.Branch, "main", "publish-1", fixture.Base);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PublishPushedAsync(request));
        Assert.Contains("has not been pushed", missing.Message);
        await fixture.GitOkAsync("-C", clone, "push", "origin", target);
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PublishPushedAsync(request));
        Assert.Contains("no commits to publish", empty.Message);

        // A checkpoint may name the base: nothing has changed yet, and nothing is recorded.
        Assert.Equal(("Verified", fixture.Base), await VerifyAsync(request));

        await fixture.CommitFileAsync(clone, "one.txt", "one");
        await fixture.GitOkAsync("-C", clone, "push", "origin", target);
        var first = (await fixture.GitAsync("-C", clone, "rev-parse", "HEAD")).Output.Trim();
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PublishPushedAsync(request));
        Assert.Contains("not at the commit named", stale.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(request));
        // Verification leaves no receipt, so the same key still publishes afterwards.
        Assert.Equal(("Verified", first), await VerifyAsync(request with { PushedSha = first }));
        // The same call serves inspection: it reports what changed since the base.
        var inspected = await fixture.Store.PublishPushedAsync(request with { PushedSha = first, VerifyOnly = true });
        Assert.Equal(new[] { "one.txt" }, inspected.ChangedFiles);
        Assert.Contains("one.txt", inspected.DiffSummary);

        async Task<(string Status, string? Commit)> VerifyAsync(InternalGitPushedPublication verify)
        {
            var verified = await fixture.Store.PublishPushedAsync(verify with { VerifyOnly = true });
            return (verified.Status, verified.CommitSha);
        }

        var published = await fixture.Store.PublishPushedAsync(request with { PushedSha = first });
        Assert.Equal(("Published", first), (published.Status, published.CommitSha));
        Assert.Equal(new[] { "one.txt" }, published.ChangedFiles);
        Assert.StartsWith("Review base: " + fixture.Base, published.DiffSummary);
        await fixture.GitOkAsync("-C", clone, "fetch", "origin");
        Assert.Equal(first, (await fixture.GitAsync("-C", clone, "rev-parse", "origin/" + Fixture.Branch)).Output.Trim());
        Assert.Equal(first, (await fixture.Store.PublishPushedAsync(request with { PushedSha = first })).CommitSha);

        await fixture.CommitFileAsync(clone, "two.txt", "two");
        await fixture.GitOkAsync("-C", clone, "push", "origin", target);
        var second = (await fixture.GitAsync("-C", clone, "rev-parse", "HEAD")).Output.Trim();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PublishPushedAsync(request with { PushedSha = first }));
        var reused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PublishPushedAsync(request with { PushedSha = second }));
        Assert.Contains("idempotency key", reused.Message);
        var next = await fixture.Store.PublishPushedAsync(request with { PushedSha = second, IdempotencyKey = "publish-2" });
        Assert.Equal(second, next.CommitSha);
        Assert.Equal(new[] { "one.txt", "two.txt" }, next.ChangedFiles.Order().ToArray());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.PublishPushedAsync(request with { Branch = "main", PushedSha = second }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.PublishPushedAsync(request with { PushedSha = "not-a-sha" }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.PublishPushedAsync(request with { IdempotencyKey = "" }));
    }

    [Fact]
    public async Task AgentModeRequiresABranchABaseCommitAndABlobLimit()
    {
        await using var fixture = await Fixture.StartAsync();
        foreach (var request in new[]
                 {
                     fixture.Request() with { AgentBaseCommitSha = null },
                     fixture.Request() with { AgentBaseCommitSha = "not-a-sha" },
                     fixture.Request() with { AgentMaximumBlobBytes = 0 },
                     fixture.Request() with { AgentBranch = "-bad" },
                     fixture.Request() with { AgentBranch = null }
                 })
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ExchangeAsync(request));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Branch = "csweet/task/0123456789abcdef0123456789abcdef/r1";
        public const int MaximumBlobBytes = 4096;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "csweet-agent-git-tests", Guid.NewGuid().ToString("N"));
        public Guid Business { get; } = Guid.NewGuid();
        public Guid Repository { get; } = Guid.NewGuid();
        public InternalGitRepositoryStore Store { get; private set; } = null!;
        public string Base { get; private set; } = "";
        private WebApplication _app = null!;
        public string Url => $"{_app.Urls.Single()}/agent.git";

        public InternalGitHttpRequest Request(string service = "git-upload-pack", bool advertise = true, byte[]? body = null) =>
            new(Business, Repository, service, advertise, body ?? [], [])
            {
                AgentBranch = Branch, AgentBaseCommitSha = Base, AgentMaximumBlobBytes = MaximumBlobBytes
            };

        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            var repositories = Path.Combine(fixture.Root, "repositories"); Directory.CreateDirectory(repositories);
            await File.WriteAllTextAsync(Path.Combine(repositories, ".csweet-git-store"), "test");
            fixture.Store = new InternalGitRepositoryStore(Options.Create(new InternalGitStorageOptions
                { RepositoryRoot = repositories, ExpectedStoreId = "test", TemporaryRoot = Path.Combine(fixture.Root, "temp") }));
            await fixture.Store.ExecuteAsync(new(fixture.Business, fixture.Repository, "create", "main"));
            var prepared = await fixture.Store.PrepareAsync(
                new(fixture.Business, fixture.Repository, Guid.NewGuid(), "main", Branch, null, "prepare"), new WorkspaceArtifactValidator());
            fixture.Base = prepared.BaseCommitSha;
            var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            fixture._app = builder.Build();
            // The smallest smart-HTTP front end: every exchange reaches GitHost in agent mode.
            fixture._app.MapMethods("/agent.git/{**path}", ["GET", "POST"], async (string path, HttpContext http, CancellationToken ct) =>
            {
                var advertise = HttpMethods.IsGet(http.Request.Method) && path == "info/refs";
                var service = advertise ? http.Request.Query["service"].ToString() : path;
                using var content = new MemoryStream();
                if (!advertise)
                {
                    using var gzip = http.Request.Headers.ContentEncoding.ToString() == "gzip"
                        ? new GZipStream(http.Request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
                    await ((Stream?)gzip ?? http.Request.Body).CopyToAsync(content, ct);
                }
                try
                {
                    var result = await fixture.Store.ExchangeAsync(fixture.Request(service, advertise, content.ToArray()), ct);
                    return Results.Bytes(result.Body, result.ContentType);
                }
                catch (ArgumentException) { return Results.BadRequest(); }
            });
            await fixture._app.StartAsync(); return fixture;
        }

        public async Task CommitFileAsync(string clone, string name, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(clone, name), content);
            await GitOkAsync("-C", clone, "add", ".");
            await GitOkAsync("-C", clone, "commit", "-m", "Add " + name);
        }
        public async Task GitOkAsync(params string[] args)
        { var result = await GitAsync(args); Assert.True(result.Code == 0, result.Output); }
        public async Task RejectedAsync(string message, params string[] args)
        { var result = await GitAsync(args); Assert.NotEqual(0, result.Code); Assert.Contains(message, result.Output); }
        public async Task<(int Code, string Output)> GitAsync(params string[] args)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            foreach (var config in new[] { "user.name=Test", "user.email=test@localhost", "credential.helper=", "core.longpaths=true", "core.autocrlf=false" })
            { start.ArgumentList.Add("-c"); start.ArgumentList.Add(config); }
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(); await _app.DisposeAsync();
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-agent-git-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path.");
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
