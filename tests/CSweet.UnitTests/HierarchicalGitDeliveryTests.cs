using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class HierarchicalGitDeliveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "csweet-hierarchical-git", Guid.NewGuid().ToString("N"));
    private readonly Guid org = Guid.NewGuid(), repository = Guid.NewGuid();
    private readonly WorkspaceArtifactValidator artifacts = new();
    private readonly InternalGitRepositoryStore store;
    public HierarchicalGitDeliveryTests()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".csweet-git-store"), "delivery-tests");
        store = new(Options.Create(new InternalGitStorageOptions { RepositoryRoot = root, ExpectedStoreId = "delivery-tests", TemporaryRoot = Path.Combine(root, "operations") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshRepositoryPromotesThroughConfiguredHierarchyAndReplaysExactly(bool epicIsolation)
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Ensure("codex/release/initial", "main");
        var target = epicIsolation ? "codex/epic/one" : "codex/release/initial";
        if (epicIsolation) await Ensure(target, "codex/release/initial");
        await Ensure("codex/story/one", target);
        var initial = await Head("main");
        var task = await Publish("codex/task/one/r1", "codex/story/one", "first.txt", "first task");
        var integrated = await Promote("codex/task/one/r1", "codex/story/one", "task-one");
        Assert.NotEqual(task.CommitSha, integrated.CandidateCommitSha); Assert.Equal(initial, await Head("main"));
        Assert.Equal("first task", await Content("codex/story/one", "first.txt"));
        // A QA failure is repaired through a NEW branch based on the integrated story.
        await Publish("codex/task/one/r2", "codex/story/one", "first.txt", "reviewed fix");
        await Promote("codex/task/one/r2", "codex/story/one", "task-one-fix");
        Assert.Equal("reviewed fix", await Content("codex/story/one", "first.txt"));
        await Promote("codex/story/one", target, "story-one");
        if (epicIsolation) await Promote(target, "codex/release/initial", "epic-one");
        var release = await Promote("codex/release/initial", "main", "release-one");
        Assert.Equal(release.CandidateCommitSha, await Head("main"));
        Assert.Equal("reviewed fix", await Content("main", "first.txt"));
        var replay = await store.DeliveryBranchAsync(Operation("codex/release/initial", "main", "promote", "release-one") with
            { ExpectedSourceSha = release.SourceCommitSha, ExpectedTargetSha = release.TargetCommitSha, CandidateCommitSha = release.CandidateCommitSha });
        Assert.True(replay.Promoted); Assert.Equal(release.CandidateCommitSha, replay.CandidateCommitSha);
        var recovered = await store.DeliveryBranchAsync(Operation("codex/release/initial", "main", "receipt", "release-one") with
            { ExpectedSourceSha = release.SourceCommitSha, ExpectedTargetSha = release.TargetCommitSha, CandidateCommitSha = release.CandidateCommitSha });
        Assert.True(recovered.Promoted); Assert.Equal(release.CandidateCommitSha, recovered.CandidateCommitSha);
    }

    [Fact]
    public async Task ConcurrentTargetChangeRejectsStaleCandidateAndAuthorBypassesAreDenied()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Ensure("codex/release/initial", "main"); await Ensure("codex/story/one", "codex/release/initial");
        await Publish("codex/task/one", "codex/story/one", "one.txt", "one");
        await Publish("codex/task/two", "codex/story/one", "two.txt", "two");
        var refs = await store.DeliveryBranchAsync(Operation("codex/task/one", "codex/story/one", "inspect", "read"));
        var candidate = await store.DeliveryBranchAsync(Operation("codex/task/one", "codex/story/one", "candidate", "stale") with
            { ExpectedSourceSha = refs.SourceCommitSha, ExpectedTargetSha = refs.TargetCommitSha });
        await Promote("codex/task/two", "codex/story/one", "two");
        var stopped = await store.DeliveryBranchAsync(Operation("codex/task/one", "codex/story/one", "promote", "stale") with
            { ExpectedSourceSha = refs.SourceCommitSha, ExpectedTargetSha = refs.TargetCommitSha, CandidateCommitSha = candidate.CandidateCommitSha });
        Assert.False(stopped.Promoted); Assert.Contains("changed", stopped.Error);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Publish("codex/story/one", "main", "bypass.txt", "bypass"));
        var current = await Head("codex/story/one");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExecuteAsync(new(org, repository, "update-ref", Ref: "refs/heads/codex/story/one", TargetSha: current, ExpectedSha: current)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExecuteAsync(new(org, repository, "delete-ref", Ref: "refs/heads/codex/story/one", ExpectedSha: current)));
    }

    [Fact]
    public async Task MultipleStoriesShareOneReleaseWithoutPromotingTasksToMain()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main")); await Ensure("codex/release/shared", "main");
        var initial = await Head("main");
        foreach (var name in new[] { "one", "two" })
        {
            await Ensure("codex/story/" + name, "codex/release/shared");
            await Publish("codex/task/" + name, "codex/story/" + name, name + ".txt", name);
            await Promote("codex/task/" + name, "codex/story/" + name, "task-" + name);
            await Promote("codex/story/" + name, "codex/release/shared", "story-" + name);
            Assert.Equal(initial, await Head("main"));
        }
        await Promote("codex/release/shared", "main", "release");
        Assert.Equal("one", await Content("main", "one.txt")); Assert.Equal("two", await Content("main", "two.txt"));
    }

    private DeliveryBranchOperation Operation(string source, string target, string operation, string key) => new(org, repository, "InternalGit", null, "", "", operation, source, target, key);
    private Task<DeliveryBranchResult> Ensure(string source, string target) => store.DeliveryBranchAsync(Operation(source, target, "ensure", "ensure:" + source));
    private async Task<DeliveryBranchResult> Promote(string source, string target, string key)
    {
        var refs = await store.DeliveryBranchAsync(Operation(source, target, "inspect", key));
        var request = Operation(source, target, "candidate", key) with { ExpectedSourceSha = refs.SourceCommitSha, ExpectedTargetSha = refs.TargetCommitSha };
        var candidate = await store.DeliveryBranchAsync(request); Assert.NotNull(candidate.CandidateCommitSha);
        var result = await store.DeliveryBranchAsync(request with { Operation = "promote", CandidateCommitSha = candidate.CandidateCommitSha });
        Assert.True(result.Promoted, result.Error); return result;
    }
    private async Task<InternalGitSnapshotResult> Publish(string branch, string target, string file, string content)
    {
        var workspace = Guid.NewGuid();
        var snapshot = await store.PrepareAsync(new(org, repository, workspace, target, branch, null, "prepare"), artifacts);
        var input = Path.Combine(root, Guid.NewGuid().ToString("N"));
        await artifacts.ExtractZipAsync(new MemoryStream(snapshot.Archive), input);
        await File.WriteAllTextAsync(Path.Combine(input, file), content);
        using var output = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, output);
        return await store.ApplySnapshotAsync(new(org, repository, workspace, "publish", snapshot.BaseCommitSha, branch, target,
            "publish", output.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, "Task implementation"), artifacts);
    }
    private async Task<string> Head(string branch) => (await store.ExecuteAsync(new(org, repository, "inspect"))).Refs.Single(x => x.Name == "refs/heads/" + branch).Sha;
    private async Task<string?> Content(string branch, string path) => (await store.ExecuteAsync(new(org, repository, "inspect", Ref: "refs/heads/" + branch, Path: path))).Content;
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
