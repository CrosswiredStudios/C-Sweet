using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    private async Task<(string Tree, IReadOnlyList<string> Paths)> ReadIntegrationMergeAsync(
        string repository, string target, string source, CancellationToken ct)
    {
        // Git exits 1 for a valid conflicted tree; fatal Git errors still throw.
        var output = await RunAsync(repository,
            ["merge-tree", "--write-tree", "--name-only", "-z", target, source], ct, allowNoMatch: true);
        var fields = output.Split('\0');
        var tree = fields[0]; ValidateSha(tree);
        var paths = fields.Skip(1).TakeWhile(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return (tree, paths);
    }

    private async Task<DeliveryBranchResult> PrepareIntegrationReworkAsync(string repository,
        DeliveryBranchOperation request, string source, string target, string key, CancellationToken ct)
    {
        if (request.ExpectedSourceSha != source || request.ExpectedTargetSha != target)
            throw new InvalidOperationException("Rework requires the exact current source and target.");
        var merge = await ReadIntegrationMergeAsync(repository, target, source, ct);
        if (merge.Paths.Count == 0)
            return new(source, target, null, Error: "The current source and target no longer conflict.");
        // Only text conflicts with both alternatives present in the merged file can be
        // materialized safely today. Binary/delete/rename conflicts stay actionable blocks.
        var marked = (await RunAsync(repository, ["grep", "-I", "-l", "-z", "-e", "^<<<<<<< ", merge.Tree, "--"],
            ct, allowNoMatch: true)).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x[(x.IndexOf(':') + 1)..]).ToHashSet(StringComparer.Ordinal);
        if (merge.Paths.Any(path => !marked.Contains(path)))
            return new(source, target, null, Error: "Integration includes a binary or structural conflict requiring explicit resolution; no branch was changed.")
                { ErrorCode = "unsupported_integration_conflict", ConflictPaths = merge.Paths };
        var reference = "refs/csweet/rework/" + key;
        var prior = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", reference], ct)).Trim();
        if (prior.Length == 0)
        {
            prior = (await RunAsync(repository, ["commit-tree", merge.Tree, "-p", target, "-p", source,
                "-m", "chore(delivery): prepare unapproved conflict workspace " + key], ct)).Trim();
            await RunAsync(repository, ["update-ref", "--stdin"], ct, input:
                $"start\nverify refs/heads/{request.SourceBranch} {source}\nverify refs/heads/{request.TargetBranch} {target}\ncreate {reference} {prior}\nprepare\ncommit\n");
        }
        var parents = (await RunAsync(repository, ["show", "-s", "--format=%P", prior], ct)).Trim();
        var tree = (await RunAsync(repository, ["rev-parse", prior + "^{tree}"], ct)).Trim();
        if (parents != target + " " + source || tree != merge.Tree)
            throw new InvalidOperationException("The rework key belongs to different source or target content.");
        return new(source, target, null) { ErrorCode = "integration_conflicts", ConflictPaths = merge.Paths, ReworkCommitSha = prior };
    }

    private async Task RequireResolvedIntegrationAsync(string repository, string baseSha, string tree, CancellationToken ct)
    {
        var bases = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", "refs/csweet/rework/"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
        if (!bases.Contains(baseSha, StringComparer.Ordinal)) return;
        var markers = await RunAsync(repository,
            ["grep", "-I", "-l", "-z", "-e", "^<<<<<<< ", "-e", "^>>>>>>> ", tree, "--"], ct, allowNoMatch: true);
        if (markers.Length > 0)
            throw new InvalidOperationException("Resolve every integration conflict marker before publishing the replacement candidate.");
    }
}
