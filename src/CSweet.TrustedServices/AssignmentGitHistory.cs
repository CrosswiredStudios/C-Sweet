using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<GitHistoryPage> ReadHistoryAsync(GitHistoryReadRequest request, CancellationToken ct = default)
    {
        ValidateHistoryRequest(request);
        using var businessLease = EnterBusinessStorage(request.OrganizationId);
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository unavailable.");
        await using var lease = await AcquireDeliveryLockAsync(repository, ct);
        var head = (await RunAsync(repository, ["rev-parse", "--verify", "refs/heads/" + request.DefaultBranch + "^{commit}"], ct)).Trim();
        ValidateSha(head);
        return await ReadHistoryCoreAsync(repository, request, head, ct);
    }

    public async Task<GitHistoryPage> ReadGitHubHistoryAsync(GitHistoryReadRequest request, GitHubRepositoryDescriptor remote,
        string token, IGitHubRepositoryTransport transport, CancellationToken ct)
    {
        ValidateHistoryRequest(request);
        using var businessLease = EnterBusinessStorage(request.OrganizationId);
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Prepare the authorized snapshot before reading history.");
        var marker = Path.Combine(repository, "csweet-github-cache");
        ValidateHistoryMetadata(marker, 1024);
        if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != "github:" + remote.RepositoryId)
            throw new IOException("GitHub cache identity does not match this repository.");
        ValidateHistoryMetadata(repository + ".github.lock", 4096);
        await using var lease = new FileStream(repository + ".github.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var refs = await transport.RefsAsync(remote, token, ct);
        var head = refs.GetValueOrDefault("refs/heads/" + request.DefaultBranch)
            ?? throw new InvalidOperationException("The default branch has no commit.");
        ValidateSha(head);
        foreach (var commit in new[] { request.AssignedCommitSha, request.PublicationCommitSha, head }.Distinct())
            await transport.FetchHistoryAsync(repository, remote, token, commit, ct);
        if ((await RunAsync(repository, ["rev-parse", "--is-shallow-repository"], ct)).Trim() != "false")
            throw new InvalidOperationException("Complete assigned history is unavailable; shallow history cannot verify ancestry.");
        return await ReadHistoryCoreAsync(repository, request, head, ct);
    }

    private static void ValidateHistoryRequest(GitHistoryReadRequest request)
    {
        if (request.OrganizationId == Guid.Empty || request.RepositoryId == Guid.Empty || request.Offset is < 0 or > 10000 || request.Limit is < 1 or > 100)
            throw new ArgumentException("Invalid history identity or page bounds.");
        ValidateSha(request.AssignedCommitSha); ValidateSha(request.PublicationCommitSha); ValidateBranch(request.DefaultBranch);
    }

    private static void ValidateHistoryMetadata(string path, long limit)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists && ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > limit))
            throw new IOException("GitHub history metadata is invalid.");
    }

    private async Task<GitHistoryPage> ReadHistoryCoreAsync(string repository, GitHistoryReadRequest request, string head, CancellationToken ct)
    {
        var ancestor = (await RunAsync(repository, ["merge-base", request.AssignedCommitSha, request.PublicationCommitSha], ct)).Trim();
        if (ancestor != request.PublicationCommitSha)
            throw new UnauthorizedAccessException("The publication is not an ancestor of the assigned commit.");
        var parents = (await RunAsync(repository, ["show", "-s", "--format=%P", request.PublicationCommitSha, "--"], ct))
            .Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parents.Length == 0) throw new InvalidOperationException("The publication has no recorded parent commit.");
        var basis = parents[0]; ValidateSha(basis);
        var common = (await RunAsync(repository, ["merge-base", head, basis], ct)).Trim();
        var output = await RunAsync(repository, ["log", "--topo-order", "--format=%H%x00%P%x00%s%x00",
            "--max-count=" + (request.Limit + 1), "--skip=" + request.Offset,
            request.AssignedCommitSha, "--"], ct);
        var fields = output.Split('\0');
        var commits = new List<GitHistoryCommit>();
        for (var index = 0; index + 2 < fields.Length; index += 3)
        {
            var sha = fields[index].Trim(); ValidateSha(sha);
            var parentShas = fields[index + 1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parentShas.Length > 64) throw new InvalidDataException("History commit exceeds parent bounds.");
            foreach (var parent in parentShas) ValidateSha(parent);
            var subject = fields[index + 2];
            commits.Add(new(sha, parentShas, subject.Length > 512 ? subject[..512] : subject, subject.Length > 512));
        }
        return new(request.RepositoryId, request.AssignedCommitSha, request.PublicationCommitSha, basis, "",
            request.DefaultBranch, head, common == head, commits.Take(request.Limit).ToArray(),
            commits.Count > request.Limit ? request.Offset + request.Limit : null);
    }
}

public sealed partial class GitHubWorkspaceOperationsService
{
    public async Task<GitHistoryPage> ReadHistoryAsync(GitHubHistoryReadRequest request, CancellationToken ct = default)
    {
        if (request.InstallationId <= 0 || request.ExternalRepositoryId <= 0) throw new ArgumentException("GitHub identity is required.");
        var remote = (await github.ListInstallationRepositoriesAsync(request.InstallationId, ct)).SingleOrDefault(repository =>
            repository.RepositoryId == request.ExternalRepositoryId && repository.Owner.Equals(request.Owner, StringComparison.OrdinalIgnoreCase) &&
            repository.Name.Equals(request.Repository, StringComparison.OrdinalIgnoreCase) && repository.IsPrivate && !repository.IsArchived)
            ?? throw new UnauthorizedAccessException("The GitHub installation cannot read this active private repository.");
        var token = await github.CreateInstallationTokenAsync(request.InstallationId, ct);
        return await store.ReadGitHubHistoryAsync(request.History, remote, token, transport, ct);
    }
}
