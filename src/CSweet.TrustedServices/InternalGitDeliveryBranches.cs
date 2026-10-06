using System.Security.Cryptography;
using System.Text;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<DeliveryBranchResult> DeliveryBranchAsync(DeliveryBranchOperation request, CancellationToken ct = default)
    {
        ValidateBranch(request.SourceBranch); ValidateBranch(request.TargetBranch);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("A bounded operation identity is required.");
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = await AcquireDeliveryLockAsync(repository, ct);
        var refs = await RefsAsync(repository, ct);
        if (request.Operation == "ensure" && refs.Count == 0)
        {
            var tree = (await RunAsync(repository, ["mktree"], ct)).Trim();
            var initial = (await RunAsync(repository, ["commit-tree", tree, "-m", "Initialize delivery repository"], ct)).Trim();
            await RunAsync(repository, ["update-ref", "refs/heads/" + request.TargetBranch, initial, new string('0', initial.Length)], ct);
            refs = await RefsAsync(repository, ct);
        }
        var source = refs.SingleOrDefault(x => x.Name == "refs/heads/" + request.SourceBranch)?.Sha;
        var target = refs.SingleOrDefault(x => x.Name == "refs/heads/" + request.TargetBranch)?.Sha
            ?? throw new InvalidOperationException("The delivery target branch is missing.");
        if (request.Operation == "ensure" && source is null)
        {
            await RunAsync(repository, ["update-ref", "refs/heads/" + request.SourceBranch, target, new string('0', target.Length)], ct);
            source = target;
        }
        if (source is null) throw new InvalidOperationException("The delivery source branch is missing.");
        if (request.Operation == "ensure")
        {
            foreach (var branch in new[] { request.SourceBranch, request.TargetBranch })
                await RunAsync(repository, ["symbolic-ref", "refs/csweet/managed/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(branch))),
                    "refs/heads/" + branch], ct);
        }
        if (request.Operation is "inspect" or "ensure") return new(source, target, null);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey))).ToLowerInvariant();
        var receiptRef = "refs/csweet/delivery/" + key;
        var receipt = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", receiptRef], ct)).Trim();
        if (request.Operation == "receipt")
        {
            if (receipt.Length == 0) return new(source, target, null);
            var receiptParents = (await RunAsync(repository, ["show", "-s", "--format=%P", receipt], ct)).Trim().Split(' ');
            if (request.CandidateCommitSha is { } accepted && accepted != receipt ||
                receiptParents.Length == 2 && (receiptParents[0] != request.ExpectedTargetSha || receiptParents[1] != request.ExpectedSourceSha))
                throw new InvalidOperationException("The promotion receipt belongs to different accepted content.");
            return new(request.ExpectedSourceSha!, request.ExpectedTargetSha!, receipt, true);
        }
        if (request.Operation == "promote" && receipt.Length > 0)
        {
            if (receipt != request.CandidateCommitSha) throw new InvalidOperationException("Promotion key belongs to a different candidate.");
            return new(request.ExpectedSourceSha!, request.ExpectedTargetSha!, receipt, true);
        }
        if ((request.ExpectedSourceSha is { } expectedSource && source != expectedSource) ||
            (request.ExpectedTargetSha is { } expectedTarget && target != expectedTarget))
            return new(source, target, null, Error: "The source or target changed; prepare and validate a new candidate.");
        if (request.Operation == "candidate")
        {
            if (source == target) return new(source, target, target);
            var candidateRef = "refs/heads/codex/candidate/" + key;
            var existing = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", candidateRef], ct)).Trim();
            if (existing.Length > 0)
            {
                var existingParents = (await RunAsync(repository, ["show", "-s", "--format=%P", existing], ct)).Trim().Split(' ');
                if (existingParents.Length != 2 || existingParents[0] != target || existingParents[1] != source)
                    throw new InvalidOperationException("The candidate operation key belongs to different source or target revisions.");
                return new(source, target, existing);
            }
            string tree;
            try { tree = (await RunAsync(repository, ["merge-tree", "--write-tree", target, source], ct)).Split('\n')[0].Trim(); }
            catch (InvalidOperationException) { return new(source, target, null, Error: "Resolve integration conflicts before validation."); }
            ValidateSha(tree);
            var candidate = (await RunAsync(repository, ["commit-tree", tree, "-p", target, "-p", source,
                "-m", "C-Sweet delivery candidate " + key], ct)).Trim();
            await RunAsync(repository, ["update-ref", candidateRef, candidate, new string('0', candidate.Length)], ct);
            return new(source, target, candidate);
        }
        if (request.Operation != "promote" || request.CandidateCommitSha is not { } commit)
            throw new ArgumentException("Unsupported delivery branch operation.");
        ValidateSha(commit);
        var parents = (await RunAsync(repository, ["show", "-s", "--format=%P", commit], ct)).Trim().Split(' ');
        if (!(source == target && commit == target) && (parents.Length != 2 || parents[0] != target || parents[1] != source))
            return new(source, target, null, Error: "The tested candidate does not bind the exact source and target.");
        if (await FindLockedChangeAsync(repository, target, commit, ct) is { } locked)
            return new(source, target, null, Error: "Integration changes locked file " + locked);
        await RunAsync(repository, ["update-ref", "--stdin"], ct, input:
            $"start\nverify refs/heads/{request.SourceBranch} {source}\nupdate refs/heads/{request.TargetBranch} {commit} {target}\ncreate {receiptRef} {commit}\nprepare\ncommit\n");
        return new(source, target, commit, true);
    }
    private static async Task<FileStream> AcquireDeliveryLockAsync(string repository, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 29) { await Task.Delay(100, ct); }
            catch (IOException error) { throw new InvalidOperationException("Another trusted integration is using this repository; retry after it completes.", error); }
        }
        throw new InvalidOperationException("Repository integration lock is unavailable.");
    }

    private async Task<IReadOnlyList<string>> ManagedBranchesAsync(string repository, CancellationToken ct) =>
        (await RunAsync(repository, ["for-each-ref", "--format=%(symref)", "refs/csweet/managed/"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(x => x[11..]).Distinct().ToArray();
}
