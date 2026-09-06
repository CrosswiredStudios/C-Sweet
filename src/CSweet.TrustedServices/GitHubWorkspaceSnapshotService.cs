using System.Diagnostics;
using System.Text;

namespace CSweet.TrustedServices;

/// <summary>
/// Fetches an exact GitHub tree into a disposable bare repository and emits a sanitized snapshot.
/// It never checks out or executes repository content, and installation tokens exist only in the
/// dedicated GitHost process environment for the duration of one Git command.
/// </summary>
public sealed class GitHubWorkspaceSnapshotService(
    GitHubAppClient github,
    WorkspaceArtifactValidator artifacts,
    IGitHubRepositoryTransport transport)
{
    public async Task<GitHubWorkspaceSnapshot> PrepareAsync(
        GitHubWorkspacePrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var repositories = await github.ListInstallationRepositoriesAsync(
            request.InstallationId, cancellationToken);
        var repository = repositories.SingleOrDefault(candidate =>
            candidate.RepositoryId == request.ExternalRepositoryId &&
            string.Equals(candidate.Owner, request.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Name, request.Repository, StringComparison.OrdinalIgnoreCase) &&
            candidate.IsPrivate && !candidate.IsArchived)
            ?? throw new UnauthorizedAccessException(
                "The repository is not an active private repository in this GitHub App installation.");

        var temporaryRoot = CreateTemporaryRoot();
        var bareRepository = Path.Combine(temporaryRoot, "repository.git");
        var rawArchive = Path.Combine(temporaryRoot, "provider.zip");
        var sanitized = Path.Combine(temporaryRoot, "sanitized");
        try
        {
            var token = await github.CreateInstallationTokenAsync(request.InstallationId, cancellationToken);
            var environment = CreateGitEnvironment(temporaryRoot, token);
            await RunGitAsync(["init", "--bare", "--template=", bareRepository], environment, cancellationToken);
            var remote = $"https://github.com/{repository.Owner}/{repository.Name}.git";
            await RunGitAsync(
                ["--git-dir", bareRepository, "remote", "add", "origin", remote],
                environment,
                cancellationToken);
            var sourceRef = request.ExpectedCommitSha;
            if (sourceRef is null)
            {
                var heads = await RunGitAsync(["--git-dir", bareRepository, "ls-remote", "--heads", "origin",
                    "refs/heads/" + request.DeterministicBranch, "refs/heads/" + request.DefaultBranch], environment, cancellationToken);
                var refs = heads.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split('\t', 2))
                    .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
                sourceRef = refs.GetValueOrDefault("refs/heads/" + request.DeterministicBranch)
                    ?? refs.GetValueOrDefault("refs/heads/" + request.DefaultBranch)
                    ?? throw new InvalidOperationException("The GitHub repository has no source branch.");
            }
            await RunGitAsync(
                ["--git-dir", bareRepository, "fetch", "--no-tags", "--depth=1", "origin", sourceRef],
                environment,
                cancellationToken);
            var head = (await RunGitAsync(
                ["--git-dir", bareRepository, "rev-parse", "FETCH_HEAD"],
                environment,
                cancellationToken)).Trim().ToLowerInvariant();
            if (!IsSha(head) ||
                request.ExpectedCommitSha is not null &&
                !string.Equals(head, request.ExpectedCommitSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub returned a commit other than the exact requested source commit.");

            Directory.CreateDirectory(Path.Combine(bareRepository, "info"));
            await File.WriteAllTextAsync(Path.Combine(bareRepository, "info", "attributes"), "* -export-ignore -export-subst\n", cancellationToken);
            await RunGitAsync(
                ["--git-dir", bareRepository, "archive", "--format=zip", $"--output={rawArchive}", "FETCH_HEAD"],
                environment,
                cancellationToken);
            await using (var source = new FileStream(
                rawArchive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await artifacts.ExtractZipAsync(source, sanitized, cancellationToken);
            }
            await GitHubWorkspaceLfs.MaterializeAsync(sanitized, bareRepository, repository, token, head, transport, cancellationToken);
            await using var output = new MemoryStream();
            var manifest = await artifacts.CreateZipAsync(sanitized, output, cancellationToken);
            return new GitHubWorkspaceSnapshot(
                $"workspace-{request.WorkspaceId:N}",
                head,
                false,
                output.ToArray(),
                manifest);
        }
        finally
        {
            DeleteTemporaryRoot(temporaryRoot);
        }
    }

    private static async Task<string> RunGitAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = timeout.Token;
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        CopyHostEnvironment(startInfo, "PATH");
        CopyHostEnvironment(startInfo, "SystemRoot");
        var firstArgument = 0;
        if (arguments.Count >= 2 && arguments[0] == "--git-dir")
        {
            startInfo.WorkingDirectory = arguments[1];
            startInfo.ArgumentList.Add("--git-dir"); startInfo.ArgumentList.Add(".");
            firstArgument = 2;
        }
        for (var index = firstArgument; index < arguments.Count; index++)
            startInfo.ArgumentList.Add(arguments[index]);
        foreach (var value in environment)
            startInfo.Environment[value.Key] = value.Value;
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The trusted Git workspace process could not start.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("Git is unavailable in the trusted GitHost runtime.", exception);
        }
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var result = new StringBuilder(); var buffer = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                if (result.Length + count > 4 * 1024 * 1024)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new InvalidOperationException("GitHub command output exceeded its limit.");
                }
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        _ = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The trusted GitHost could not materialize the requested source revision.");
        return await output;
    }

    private static void CopyHostEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            startInfo.Environment[name] = value;
    }

    private static IReadOnlyDictionary<string, string> CreateGitEnvironment(
        string temporaryRoot,
        string token)
    {
        var home = Path.Combine(temporaryRoot, "home");
        Directory.CreateDirectory(home);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["TMPDIR"] = temporaryRoot,
            ["TEMP"] = temporaryRoot,
            ["TMP"] = temporaryRoot,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_LFS_SKIP_SMUDGE"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_COUNT"] = "11",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"x-access-token:{token}")),
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = string.Empty,
            ["GIT_CONFIG_KEY_2"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_2"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_KEY_3"] = "protocol.file.allow",
            ["GIT_CONFIG_VALUE_3"] = "never",
            ["GIT_CONFIG_KEY_4"] = "protocol.ext.allow",
            ["GIT_CONFIG_VALUE_4"] = "never",
            ["GIT_CONFIG_KEY_5"] = "submodule.recurse",
            ["GIT_CONFIG_VALUE_5"] = "false",
            ["GIT_CONFIG_KEY_6"] = "fetch.recurseSubmodules",
            ["GIT_CONFIG_VALUE_6"] = "false",
            ["GIT_CONFIG_KEY_7"] = "http.followRedirects", ["GIT_CONFIG_VALUE_7"] = "false",
            ["GIT_CONFIG_KEY_8"] = "protocol.allow", ["GIT_CONFIG_VALUE_8"] = "never",
            ["GIT_CONFIG_KEY_9"] = "protocol.https.allow", ["GIT_CONFIG_VALUE_9"] = "always",
            ["GIT_CONFIG_KEY_10"] = "core.longpaths", ["GIT_CONFIG_VALUE_10"] = "true"
        };
    }

    private static void Validate(GitHubWorkspacePrepareRequest request)
    {
        if (request.InstallationId <= 0 || request.ExternalRepositoryId <= 0 || request.WorkspaceId == Guid.Empty)
            throw new ArgumentException("A GitHub installation, repository identity, and workspace are required.");
        InternalGitRepositoryStore.ValidateBranch(request.DefaultBranch);
        InternalGitRepositoryStore.ValidateBranch(request.DeterministicBranch);
        if (!IsCoordinate(request.Owner) || !IsCoordinate(request.Repository) ||
            !IsBranch(request.DefaultBranch) || !IsBranch(request.DeterministicBranch))
            throw new ArgumentException("The repository or branch coordinates are invalid.");
        if (request.ExpectedCommitSha is not null && !IsSha(request.ExpectedCommitSha))
            throw new ArgumentException("The expected source commit is invalid.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("The idempotency key is invalid.");
    }

    private static bool IsCoordinate(string value) =>
        value.Length is >= 1 and <= 100 && value is not ("." or "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsBranch(string value) =>
        value.Length is >= 1 and <= 255 &&
        !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/');

    private static bool IsSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-githost-workspaces");
        Directory.CreateDirectory(root);
        var result = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(result);
        return result;
    }

    private static void DeleteTemporaryRoot(string temporaryRoot)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "csweet-githost-workspaces"));
        var resolved = Path.GetFullPath(temporaryRoot);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a path outside the GitHost workspace root.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
