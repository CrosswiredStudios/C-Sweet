using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed class GitHubWorkspaceOperationsService(GitHubAppClient github, InternalGitRepositoryStore store,
    WorkspaceArtifactValidator artifacts, IGitHubRepositoryTransport transport)
{
    public async Task<GitHubSnapshotResult> ApplyAsync(GitHubSnapshotOperation request, CancellationToken ct = default)
    {
        if (request.InstallationId <= 0 || request.ExternalRepositoryId <= 0) throw new ArgumentException("GitHub repository identity is required.");
        var repository = (await github.ListInstallationRepositoriesAsync(request.InstallationId, ct)).SingleOrDefault(r =>
            r.RepositoryId == request.ExternalRepositoryId && r.Owner.Equals(request.Owner, StringComparison.OrdinalIgnoreCase) &&
            r.Name.Equals(request.Repository, StringComparison.OrdinalIgnoreCase) && r.IsPrivate && !r.IsArchived)
            ?? throw new UnauthorizedAccessException("The GitHub installation cannot access this active private repository.");
        if (request.Workspace.Operation == "publish" && (string.IsNullOrWhiteSpace(request.ProposedChangeTitle) || request.ProposedChangeTitle.Length > 256 || request.ProposedChangeBody?.Length > 32768))
            throw new ArgumentException("A bounded proposed-change title and body are required.");
        var token = await github.CreateInstallationTokenAsync(request.InstallationId, ct);
        var result = await store.ApplyGitHubSnapshotAsync(request, repository, token, artifacts, transport, ct);
        var url = result.Status == "Published" ? await github.EnsureWorkspacePullRequestAsync(request, result.CommitSha!, token, ct) : null;
        return new(result, url);
    }
}

public sealed partial class InternalGitRepositoryStore
{
    private sealed record GitHubPublicationAttempt(string RequestHash, string? ExpectedHead, bool PushAttempted);
    public async Task<InternalGitSnapshotResult> ApplyGitHubSnapshotAsync(GitHubSnapshotOperation operation, GitHubRepositoryDescriptor remote,
        string token, WorkspaceArtifactValidator artifacts, IGitHubRepositoryTransport transport, CancellationToken ct = default)
    {
        var request = operation.Workspace with { AllowLfs = false };
        ValidateBranch(request.Branch); ValidateBranch(request.DefaultBranch); ValidateSha(request.BaseSha);
        if (request.Operation is not ("inspect" or "publish" or "refresh") || request.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160 ||
            (request.Operation == "publish" && request.Branch == request.DefaultBranch)) throw new ArgumentException("Invalid GitHub workspace operation.");
        var cache = RepositoryPath(request.OrganizationId, request.RepositoryId);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        ValidateMetadata(cache + ".github.lock", 4096);
        await using var lease = new FileStream(cache + ".github.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var marker = Path.Combine(cache, "csweet-github-cache"); var identity = "github:" + remote.RepositoryId;
        ValidateMetadata(marker, 1024);
        if (Directory.Exists(cache))
        {
            if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != identity) throw new IOException("GitHub cache identity does not match this repository.");
        }
        else
        {
            await ExecuteAsync(new(request.OrganizationId, request.RepositoryId, "create", request.DefaultBranch), ct);
            await File.WriteAllTextAsync(marker, identity, ct);
        }
        var refs = await transport.RefsAsync(remote, token, ct);
        var head = refs.GetValueOrDefault("refs/heads/" + request.Branch);
        var target = refs.GetValueOrDefault("refs/heads/" + request.DefaultBranch) ?? throw new InvalidOperationException("The GitHub default branch has no commit.");
        ValidateSha(target); if (head is not null) ValidateSha(head);
        foreach (var sha in new[] { request.BaseSha, target, head }.OfType<string>().Distinct()) await transport.FetchAsync(cache, remote, token, sha, ct);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.WorkspaceId:N}:{request.IdempotencyKey}"))).ToLowerInvariant();
        var receipt = (await RunAsync(cache, ["for-each-ref", "--format=%(objectname)", "refs/csweet/publications/" + key], ct)).Trim();
        var attemptPath = Path.Combine(cache, "csweet-github-attempt-" + key + ".json");
        ValidateMetadata(attemptPath, 8192);
        GitHubPublicationAttempt? attempt = null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.BaseSha, request.ArchiveManifestSha,
            request.CommitMessage, request.Branch, request.DefaultBranch, operation.ProposedChangeTitle, operation.ProposedChangeBody }))));
        if (request.Operation == "publish")
        {
            if (File.Exists(attemptPath))
            {
                attempt = JsonSerializer.Deserialize<GitHubPublicationAttempt>(await File.ReadAllTextAsync(attemptPath, ct)) ?? throw new IOException("Publication receipt is invalid.");
                if (attempt.RequestHash != hash) throw new InvalidOperationException("The publication key was already used for different content.");
                if (attempt.PushAttempted && head != receipt) throw new InvalidOperationException("The previous push outcome is ambiguous or its branch changed. Refresh and use a new publication key.");
            }
            else
            {
                if (receipt.Length > 0 || (head is not null && head != request.BaseSha)) throw new InvalidOperationException("The GitHub work branch changed. Refresh before publishing.");
                attempt = new(hash, head, false); await WriteAttemptAsync(attempt);
            }
        }
        await RunAsync(cache, ["update-ref", "refs/heads/" + request.DefaultBranch, target], ct);
        if (receipt.Length == 0 || request.Operation != "publish")
        {
            if (head is null) await RunAsync(cache, ["update-ref", "-d", "refs/heads/" + request.Branch], ct);
            else await RunAsync(cache, ["update-ref", "refs/heads/" + request.Branch, head], ct);
        }
        var result = await ApplySnapshotAsync(request, artifacts, ct);
        if (request.Operation != "publish") return result with { LatestTargetSha = head ?? target };
        if (result.Status != "Published") return result;
        if (head != result.CommitSha)
        {
            if (head != attempt!.ExpectedHead) throw new InvalidOperationException("The GitHub branch changed before publication.");
            await WriteAttemptAsync(attempt with { PushAttempted = true });
            await transport.PushAsync(cache, remote, token, request.Branch, result.CommitSha!, attempt.ExpectedHead, ct);
            if ((await transport.RefsAsync(remote, token, ct)).GetValueOrDefault("refs/heads/" + request.Branch) != result.CommitSha)
                throw new InvalidOperationException("GitHub did not confirm the exact published commit.");
        }
        return result with { LatestTargetSha = target };

        static void ValidateMetadata(string path, long limit)
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null || (file.Exists && ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > limit)))
                throw new IOException("GitHub workspace metadata is invalid.");
        }

        async Task WriteAttemptAsync(GitHubPublicationAttempt value)
        {
            var temp = attemptPath + "." + Guid.NewGuid().ToString("N");
            try { await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await JsonSerializer.SerializeAsync(output, value, cancellationToken: ct); output.Flush(true); } File.Move(temp, attemptPath, true); }
            finally { File.Delete(temp); }
        }
    }
}
