using System.Security.Cryptography;
using System.Text;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    /// <summary>
    /// Publishes the commits an agent pushed to its work branch. Unlike snapshot publication this
    /// creates no commit: it accepts the branch tip only when it is exactly the commit the agent named
    /// and that commit descends from the assigned base, then records the same publication receipt.
    /// The branch cannot move between the check and the receipt: both are in one ref transaction.
    /// </summary>
    public async Task<InternalGitSnapshotResult> PublishPushedAsync(InternalGitPushedPublication request, CancellationToken ct = default)
    {
        ValidateSha(request.BaseSha); ValidateSha(request.PushedSha); ValidateBranch(request.Branch); ValidateBranch(request.DefaultBranch);
        if (request.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("Invalid workspace operation.");
        if (request.Branch == request.DefaultBranch)
            throw new UnauthorizedAccessException("Publication cannot write directly to the default branch.");
        var baseSha = request.BaseSha.ToLowerInvariant(); var pushed = request.PushedSha.ToLowerInvariant();
        using var businessLease = EnterBusinessStorage(request.OrganizationId);
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if ((await ManagedBranchesAsync(repository, ct)).Contains(request.Branch, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Managed integration branches accept only trusted delivery promotion; publish a task branch instead.");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.WorkspaceId:N}:{request.IdempotencyKey}"))).ToLowerInvariant();
        var receiptRef = "refs/csweet/publications/" + identity;
        var refs = await RefsAsync(repository, ct);
        var source = refs.SingleOrDefault(r => r.Name == "refs/heads/" + request.Branch)?.Sha;
        var target = refs.SingleOrDefault(r => r.Name == "refs/heads/" + request.DefaultBranch)?.Sha;
        if (source is null) throw new InvalidOperationException("The work branch has not been pushed.");
        if (!string.Equals(source, pushed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The work branch is not at the commit named for publication. Push, then publish the pushed commit.");
        if (pushed == baseSha) throw new InvalidOperationException("The work branch holds no commits to publish.");
        string ancestor;
        try { ancestor = (await RunAsync(repository, ["merge-base", baseSha, pushed], ct)).Trim(); }
        catch (InvalidOperationException) { ancestor = ""; }
        if (!string.Equals(ancestor, baseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The work branch does not descend from its assigned base commit.");
        var tree = (await RunAsync(repository, ["rev-parse", pushed + "^{tree}"], ct)).Trim();
        ValidateSha(tree);
        await RequireResolvedIntegrationAsync(repository, baseSha, tree, ct);

        // Review the complete proposal against the integration target, as snapshot publication does.
        var reviewBase = target is null ? baseSha : (await RunAsync(repository, ["merge-base", target, baseSha], ct)).Trim();
        ValidateSha(reviewBase);
        var changed = (await RunAsync(repository, ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", reviewBase, tree, "--"], ct))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var summary = $"Review base: {reviewBase}\n" + await RunAsync(repository,
            ["diff", "--no-ext-diff", "--no-textconv", "--no-color", reviewBase, tree, "--"], ct);

        var receipt = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", receiptRef], ct)).Trim();
        if (receipt.Length > 0)
        {
            if (!string.Equals(receipt, pushed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The idempotency key was already used with different content.");
            return new("Published", baseSha, pushed, changed, summary, target);
        }
        if (await FindLockedChangeAsync(repository, baseSha, tree, ct, request.LockOwnerId) is { } lockedPath)
            return new("Locked", baseSha, null, changed, $"Publication changes locked file {lockedPath}. Ask its owner to release the lock before publishing.", target);
        await RunAsync(repository, ["update-ref", "--stdin"], ct, input:
            $"start\nverify refs/heads/{request.Branch} {pushed}\ncreate {receiptRef} {pushed}\nprepare\ncommit\n");
        return new("Published", baseSha, pushed, changed, summary, target);
    }
}
