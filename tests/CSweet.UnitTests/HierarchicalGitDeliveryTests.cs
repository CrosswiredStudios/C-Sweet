using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class HierarchicalGitDeliveryTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapProtectsOnlyItsNewBranchAndRetainsAnyExistingBaselineProtection(bool baseAlreadyManaged)
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Publish("seed", "main", "seed.txt", "seed");
        if (baseAlreadyManaged) await Ensure("release-existing", "main");
        var baseline = await Head("main");
        await store.DeliveryBranchAsync(Operation("isolated", "main", "bootstrap", "isolated-base"));
        await Ensure("isolated", "isolated");
        Assert.Equal(baseline, await Head("isolated"));
        var writeBase = new InternalGitRepositoryRequest(org, repository, "update-ref", Ref: "refs/heads/main", TargetSha: baseline, ExpectedSha: baseline);
        if (baseAlreadyManaged)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExecuteAsync(writeBase));
        else await store.ExecuteAsync(writeBase);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExecuteAsync(new(org, repository,
            "update-ref", Ref: "refs/heads/isolated", TargetSha: baseline, ExpectedSha: baseline)));
        await Publish("implementation", "isolated", "delivered.txt", "reviewed work");
        await Promote("implementation", "isolated", "integrate");
        var integrated = await Head("isolated");
        await store.DeliveryBranchAsync(Operation("isolated", "main", "bootstrap", "isolated-base"));
        Assert.Equal(integrated, await Head("isolated")); Assert.Equal(baseline, await Head("main"));
    }

    [Fact]
    public async Task SuccessorIncrementInheritsAcceptedScaffoldWhileDefaultBranchStaysUnpromoted()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        var integration = "csweet/integration/" + Guid.NewGuid().ToString("N");
        await Ensure(integration, "main"); var main = await Head("main");
        await Ensure("csweet/release/scaffold", integration);
        await Ensure("csweet/story/scaffold", "csweet/release/scaffold");
        await Publish("csweet/task/scaffold", "csweet/story/scaffold", "scaffold.txt", "accepted scaffold");
        await Promote("csweet/task/scaffold", "csweet/story/scaffold", "task-scaffold");
        await Promote("csweet/story/scaffold", "csweet/release/scaffold", "story-scaffold");
        await Promote("csweet/release/scaffold", integration, "increment-scaffold");
        // Host provisioning on reconnect must keep the accepted integration head.
        var accepted = await Head(integration); await Ensure(integration, "main"); Assert.Equal(accepted, await Head(integration));
        await Ensure("csweet/release/gameplay", integration);
        await Ensure("csweet/story/gameplay", "csweet/release/gameplay");
        Assert.Equal("accepted scaffold", await Content("csweet/story/gameplay", "scaffold.txt"));
        await Publish("csweet/task/gameplay", "csweet/story/gameplay", "gameplay.txt", "next increment");
        await Promote("csweet/task/gameplay", "csweet/story/gameplay", "task-gameplay");
        await Promote("csweet/story/gameplay", "csweet/release/gameplay", "story-gameplay");
        await Promote("csweet/release/gameplay", integration, "increment-gameplay");
        Assert.Equal("accepted scaffold", await Content(integration, "scaffold.txt"));
        Assert.Equal("next increment", await Content(integration, "gameplay.txt"));
        Assert.Equal(main, await Head("main"));
    }

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
        await Ensure("csweet/release/initial", "main");
        var target = epicIsolation ? "csweet/epic/one" : "csweet/release/initial";
        if (epicIsolation) await Ensure(target, "csweet/release/initial");
        await Ensure("csweet/story/one", target);
        var initial = await Head("main");
        var task = await Publish("csweet/task/one/r1", "csweet/story/one", "first.txt", "first task");
        var integrated = await Promote("csweet/task/one/r1", "csweet/story/one", "task-one");
        Assert.NotEqual(task.CommitSha, integrated.CandidateCommitSha); Assert.Equal(initial, await Head("main"));
        Assert.Equal("first task", await Content("csweet/story/one", "first.txt"));
        // A QA failure is repaired through a NEW branch based on the integrated story.
        await Publish("csweet/task/one/r2", "csweet/story/one", "first.txt", "reviewed fix");
        await Promote("csweet/task/one/r2", "csweet/story/one", "task-one-fix");
        Assert.Equal("reviewed fix", await Content("csweet/story/one", "first.txt"));
        await Promote("csweet/story/one", target, "story-one");
        if (epicIsolation) await Promote(target, "csweet/release/initial", "epic-one");
        var release = await Promote("csweet/release/initial", "main", "release-one");
        Assert.Equal(release.CandidateCommitSha, await Head("main"));
        Assert.Equal("reviewed fix", await Content("main", "first.txt"));
        var replay = await store.DeliveryBranchAsync(Operation("csweet/release/initial", "main", "promote", "release-one") with
            { ExpectedSourceSha = release.SourceCommitSha, ExpectedTargetSha = release.TargetCommitSha, CandidateCommitSha = release.CandidateCommitSha });
        Assert.True(replay.Promoted); Assert.Equal(release.CandidateCommitSha, replay.CandidateCommitSha);
        var recovered = await store.DeliveryBranchAsync(Operation("csweet/release/initial", "main", "receipt", "release-one") with
            { ExpectedSourceSha = release.SourceCommitSha, ExpectedTargetSha = release.TargetCommitSha, CandidateCommitSha = release.CandidateCommitSha });
        Assert.True(recovered.Promoted); Assert.Equal(release.CandidateCommitSha, recovered.CandidateCommitSha);
    }

    [Fact]
    public async Task PreIntegrationReworkPreservesRejectedCodeAndPublishesAnIncrementalFix()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Ensure("csweet/story/one", "main");
        var first = await Publish("csweet/task/one/r1", "csweet/story/one", "implementation.txt", "existing implementation");
        await Assert.ThrowsAsync<ArgumentException>(() => Content("csweet/story/one", "implementation.txt"));
        var workspace = Guid.NewGuid();
        var snapshot = await store.PrepareAsync(new(org, repository, workspace, "csweet/story/one",
            "csweet/task/one/r2", first.CommitSha, "rework"), artifacts);
        Assert.Equal(first.CommitSha, snapshot.BaseCommitSha);
        var input = Path.Combine(root, "rework");
        await artifacts.ExtractZipAsync(new MemoryStream(snapshot.Archive), input);
        Assert.Equal("existing implementation", await File.ReadAllTextAsync(Path.Combine(input, "implementation.txt")));
        await File.WriteAllTextAsync(Path.Combine(input, "review-fix.txt"), "addressed review finding");
        using var output = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, output);
        await store.ApplySnapshotAsync(new(org, repository, workspace, "publish", snapshot.BaseCommitSha,
            "csweet/task/one/r2", "csweet/story/one", "rework-publish", output.ToArray(), manifest.Sha256,
            manifest.FileCount, manifest.TotalBytes, "Address review feedback"), artifacts);
        Assert.Equal("existing implementation", await Content("csweet/task/one/r2", "implementation.txt"));
        Assert.Equal("addressed review finding", await Content("csweet/task/one/r2", "review-fix.txt"));
        await Promote("csweet/task/one/r2", "csweet/story/one", "rework-integrate");
        Assert.Equal("existing implementation", await Content("csweet/story/one", "implementation.txt"));
    }

    [Fact]
    public async Task ConflictedReworkPreservesBothHistoriesAndRequiresResolutionBeforePublication()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Ensure("csweet/story/one", "main");
        await Publish("csweet/task/base", "csweet/story/one", "shared.txt", "base\n");
        await Promote("csweet/task/base", "csweet/story/one", "base");
        var task = await Publish("csweet/task/one/r1", "csweet/story/one", "shared.txt", "task feature\n");
        await Publish("csweet/task/two", "csweet/story/one", "shared.txt", "story feature\n");
        await Promote("csweet/task/two", "csweet/story/one", "two");
        var target = await Head("csweet/story/one");
        var request = Operation("csweet/task/one/r1", "csweet/story/one", "candidate", "conflicted") with
            { ExpectedSourceSha = task.CommitSha, ExpectedTargetSha = target };
        var stopped = await store.DeliveryBranchAsync(request);
        Assert.Equal("integration_conflicts", stopped.ErrorCode);
        Assert.Equal("shared.txt", Assert.Single(stopped.ConflictPaths));
        Assert.Null(stopped.CandidateCommitSha);
        var rework = await store.DeliveryBranchAsync(request with { Operation = "rework" });
        Assert.Equal(rework.ReworkCommitSha, (await store.DeliveryBranchAsync(request with { Operation = "rework" })).ReworkCommitSha);
        Assert.Null(rework.CandidateCommitSha); Assert.False(rework.Promoted);
        Assert.Equal(target, await Head("csweet/story/one"));
        Assert.Equal(task.CommitSha, await Head("csweet/task/one/r1"));
        var workspace = Guid.NewGuid();
        var snapshot = await store.PrepareAsync(new(org, repository, workspace, "csweet/story/one",
            "csweet/task/one/r2", rework.ReworkCommitSha, "rework"), artifacts);
        var directory = Path.Combine(root, "conflict-resolution");
        await artifacts.ExtractZipAsync(new MemoryStream(snapshot.Archive), directory);
        var content = await File.ReadAllTextAsync(Path.Combine(directory, "shared.txt"));
        Assert.Contains("<<<<<<<", content); Assert.Contains("task feature", content); Assert.Contains("story feature", content);
        async Task<InternalGitSnapshotResult> PublishResolved()
        {
            using var archive = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(directory, archive);
            return await store.ApplySnapshotAsync(new(org, repository, workspace, "publish", snapshot.BaseCommitSha,
                "csweet/task/one/r2", "csweet/story/one", "resolved", archive.ToArray(), manifest.Sha256,
                manifest.FileCount, manifest.TotalBytes, "Resolve integration conflicts"), artifacts);
        }
        Assert.Contains("conflict marker", (await Assert.ThrowsAsync<InvalidOperationException>(PublishResolved)).Message);
        await File.WriteAllTextAsync(Path.Combine(directory, "shared.txt"), "story feature\ntask feature\n");
        await PublishResolved();
        var integrated = await Promote("csweet/task/one/r2", "csweet/story/one", "resolved");
        Assert.True(integrated.Promoted);
        Assert.Equal("story feature\ntask feature\n", await Content("csweet/story/one", "shared.txt"));
        var stale = await store.DeliveryBranchAsync(request with { Operation = "rework" });
        Assert.Null(stale.ReworkCommitSha); Assert.Contains("changed", stale.Error);
    }

    [Fact]
    public async Task BinaryConflictStopsWithoutDiscardingEitherBranch()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Ensure("csweet/story/one", "main");
        await Publish("csweet/task/base", "csweet/story/one", "shared.bin", "\0base");
        await Promote("csweet/task/base", "csweet/story/one", "base");
        var task = await Publish("csweet/task/one", "csweet/story/one", "shared.bin", "\0task");
        await Publish("csweet/task/two", "csweet/story/one", "shared.bin", "\0story");
        await Promote("csweet/task/two", "csweet/story/one", "two");
        var target = await Head("csweet/story/one");
        var result = await store.DeliveryBranchAsync(Operation("csweet/task/one", "csweet/story/one", "rework", "binary") with
            { ExpectedSourceSha = task.CommitSha, ExpectedTargetSha = target });
        Assert.Equal("unsupported_integration_conflict", result.ErrorCode); Assert.Null(result.ReworkCommitSha);
        Assert.Equal(task.CommitSha, await Head("csweet/task/one")); Assert.Equal(target, await Head("csweet/story/one"));
    }

    [Fact]
    public async Task ConcurrentTargetChangeRejectsStaleCandidateAndAuthorBypassesAreDenied()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main"));
        await Ensure("csweet/release/initial", "main"); await Ensure("csweet/story/one", "csweet/release/initial");
        await Publish("csweet/task/one", "csweet/story/one", "one.txt", "one");
        await Publish("csweet/task/two", "csweet/story/one", "two.txt", "two");
        var refs = await store.DeliveryBranchAsync(Operation("csweet/task/one", "csweet/story/one", "inspect", "read"));
        var candidate = await store.DeliveryBranchAsync(Operation("csweet/task/one", "csweet/story/one", "candidate", "stale") with
            { ExpectedSourceSha = refs.SourceCommitSha, ExpectedTargetSha = refs.TargetCommitSha });
        await Promote("csweet/task/two", "csweet/story/one", "two");
        var stopped = await store.DeliveryBranchAsync(Operation("csweet/task/one", "csweet/story/one", "promote", "stale") with
            { ExpectedSourceSha = refs.SourceCommitSha, ExpectedTargetSha = refs.TargetCommitSha, CandidateCommitSha = candidate.CandidateCommitSha });
        Assert.False(stopped.Promoted); Assert.Contains("changed", stopped.Error);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Publish("csweet/story/one", "main", "bypass.txt", "bypass"));
        var current = await Head("csweet/story/one");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExecuteAsync(new(org, repository, "update-ref", Ref: "refs/heads/csweet/story/one", TargetSha: current, ExpectedSha: current)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExecuteAsync(new(org, repository, "delete-ref", Ref: "refs/heads/csweet/story/one", ExpectedSha: current)));
    }

    [Fact]
    public async Task MultipleStoriesShareOneReleaseWithoutPromotingTasksToMain()
    {
        await store.ExecuteAsync(new(org, repository, "create", "main")); await Ensure("csweet/release/shared", "main");
        var initial = await Head("main");
        foreach (var name in new[] { "one", "two" })
        {
            await Ensure("csweet/story/" + name, "csweet/release/shared");
            await Publish("csweet/task/" + name, "csweet/story/" + name, name + ".txt", name);
            await Promote("csweet/task/" + name, "csweet/story/" + name, "task-" + name);
            await Promote("csweet/story/" + name, "csweet/release/shared", "story-" + name);
            Assert.Equal(initial, await Head("main"));
        }
        await Promote("csweet/release/shared", "main", "release");
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
