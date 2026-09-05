using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CSweet.Contracts.SourceControl;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

/// <summary>Runs bounded Git commands with no caller-selected executable, argument list, or storage path.</summary>
public sealed partial class InternalGitRepositoryStore(IOptions<InternalGitStorageOptions> options)
{
    private readonly InternalGitStorageOptions _options = options.Value;

    public async Task<InternalGitStorageStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        string? error = null;
        try
        {
            VerifyRoot();
            var probe = Path.Combine(_options.RepositoryRoot, ".probe-" + Guid.NewGuid().ToString("N"));
            var renamed = probe + ".renamed";
            try
            {
                await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(probe, renamed);
            }
            finally { File.Delete(probe); File.Delete(renamed); }
            _ = await RunAsync(null, ["--version"], cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { error = ex.Message; }
        return new(error is null, _options.RepositoryRoot, _options.TemporaryRoot,
            _options.Lfs.Provider, _options.Lfs.Location(Path.GetFullPath(Path.Combine(_options.RepositoryRoot, "..", "lfs"))),
            _options.Backup.Provider, _options.Backup.Location(Path.GetFullPath(Path.Combine(_options.RepositoryRoot, "..", "backups"))), error);
    }

    private void VerifyRoot()
    {
        _options.Validate();
        var root = Path.GetFullPath(_options.RepositoryRoot);
        var marker = Path.Combine(root, ".csweet-git-store");
        if (_options.ExpectedStoreId is not null)
        {
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != _options.ExpectedStoreId)
                throw new IOException("Repository storage is unavailable or its identity does not match. Check the NAS mount.");
        }
        else
        {
            var defaultRoot = Path.GetFullPath(new InternalGitStorageOptions().RepositoryRoot);
            if (!string.Equals(root, defaultRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Custom storage requires ExpectedStoreId and a matching .csweet-git-store marker provisioned by the operator.");
            Directory.CreateDirectory(root);
        }
        RejectLink(root);
    }

    private static void RejectLink(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Repository storage cannot contain symbolic links.");
    }

    private string RepositoryPath(Guid organizationId, Guid repositoryId)
    {
        if (organizationId == Guid.Empty || repositoryId == Guid.Empty) throw new ArgumentException("Repository identifiers are required.");
        VerifyRoot();
        var parent = Path.Combine(Path.GetFullPath(_options.RepositoryRoot), organizationId.ToString("N"));
        var path = Path.Combine(parent, repositoryId.ToString("N") + ".git");
        RejectLink(parent); RejectLink(path);
        return path;
    }

    public async Task<InternalGitRepositoryInspection> ExecuteAsync(InternalGitRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var path = RepositoryPath(request.OrganizationId, request.RepositoryId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (request.Operation == "delete")
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            return new("", [], [], []);
        }
        if (request.Operation == "create")
        {
            ValidateBranch(request.Name);
            if (!Directory.Exists(path))
            {
                var staging = path + ".creating";
                if (Directory.Exists(staging)) throw new IOException("Interrupted repository creation requires recovery.");
                await RunAsync(null, ["init", "--bare", "--template=", $"--initial-branch={request.Name}", staging], cancellationToken);
                Directory.Move(staging, path);
            }
        }
        else if (!Directory.Exists(path)) throw new KeyNotFoundException("Internal repository does not exist.");

        switch (request.Operation)
        {
            case "compare":
                ValidateBranch(request.Name); ValidateSha(request.ExpectedSha);
                var targetRef = "refs/heads/" + request.Name;
                if (request.TargetSha is not null)
                {
                    ValidateSha(request.TargetSha);
                    targetRef = request.TargetSha + "^1"; // Preserve the reviewed diff after the proposal has merged.
                }
                var mergeBase = (await RunAsync(path, ["merge-base", targetRef, request.ExpectedSha!], cancellationToken)).Trim();
                ValidateSha(mergeBase);
                var changes = (await RunAsync(path, ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", mergeBase, request.ExpectedSha!, "--"], cancellationToken))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var patch = await RunAsync(path, ["diff", "--no-ext-diff", "--no-textconv", mergeBase, request.ExpectedSha!, "--"], cancellationToken);
                return new(request.Name!, [], [], changes, patch);
            case "create": case "inspect": break;
            case "default-branch":
                ValidateBranch(request.Name);
                var refs = await RefsAsync(path, cancellationToken);
                if (refs.Count > 0 && !refs.Any(r => r.Name == $"refs/heads/{request.Name}"))
                    throw new ArgumentException("The default branch must exist in a nonempty repository.");
                await RunAsync(path, ["symbolic-ref", "HEAD", $"refs/heads/{request.Name}"], cancellationToken);
                break;
            case "update-ref":
                ValidateRef(request.Ref); ValidateSha(request.TargetSha); ValidateSha(request.ExpectedSha);
                await EnsureRefUnlockedAsync(path, request.ExpectedSha!, request.TargetSha!, cancellationToken);
                await RunAsync(path, ["update-ref", request.Ref!, request.TargetSha!, request.ExpectedSha!], cancellationToken);
                break;
            case "delete-ref":
                ValidateRef(request.Ref); ValidateSha(request.ExpectedSha);
                var head = (await RunAsync(path, ["symbolic-ref", "HEAD"], cancellationToken)).Trim();
                if (head == request.Ref) throw new ArgumentException("Change the default branch before deleting it.");
                await EnsureRefUnlockedAsync(path, request.ExpectedSha!, new string('0', 40), cancellationToken);
                await RunAsync(path, ["update-ref", "-d", request.Ref!, request.ExpectedSha!], cancellationToken);
                break;
            default: throw new ArgumentException("Unsupported internal Git operation.");
        }
        return await InspectAsync(path, request.Operation == "inspect" ? request.Ref : null, request.Path, cancellationToken);
    }

    private async Task<List<InternalGitRef>> RefsAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RunAsync(path, ["for-each-ref", "--format=%(refname) %(objectname)", "refs/heads/", "refs/tags/"], cancellationToken);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split(' ', 2))
            .Select(parts => new InternalGitRef(parts[0], parts[1])).ToList();
    }

    private async Task<InternalGitRepositoryInspection> InspectAsync(string path, string? requestedRef, string? file,
        CancellationToken cancellationToken)
    {
        var branch = (await RunAsync(path, ["symbolic-ref", "--short", "HEAD"], cancellationToken)).Trim();
        var refs = await RefsAsync(path, cancellationToken);
        if (refs.Count == 0) return new(branch, refs, [], []);
        var selected = requestedRef ?? $"refs/heads/{branch}";
        var commit = refs.SingleOrDefault(r => r.Name == selected)?.Sha
            ?? throw new ArgumentException("Select an existing branch or tag.");
        var log = await RunAsync(path, ["log", "-30", "--format=%H%x09%an%x09%s", commit, "--"], cancellationToken);
        var commits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd().Split('\t', 3))
            .Where(p => p.Length == 3).Select(p => new InternalGitCommit(p[0], p[1], p[2])).ToList();
        var tree = await RunAsync(path, ["ls-tree", "-r", "--name-only", "-z", commit], cancellationToken);
        var files = tree.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string? content = null;
        if (file is not null)
        {
            if (!files.Contains(file, StringComparer.Ordinal)) throw new ArgumentException("File is not present in the selected revision.");
            content = await RunAsync(path, ["show", $"{commit}:{file}"], cancellationToken);
        }
        return new(branch, refs, commits, files, content);
    }

    public static void ValidateBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 200 || branch.StartsWith('-'))
            throw new ArgumentException("Invalid branch name.");
        ValidateRef("refs/heads/" + branch);
    }

    public static void ValidateRef(string? reference)
    {
        if (reference is null || reference.Length > 255 ||
            !(reference.StartsWith("refs/heads/", StringComparison.Ordinal) || reference.StartsWith("refs/tags/", StringComparison.Ordinal)) ||
            reference.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c)) ||
            reference.Contains("..") || reference.Contains("@{") ||
            reference.Split('/').Any(p => p.Length == 0 || p.StartsWith('.') || p.EndsWith('.') || p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Invalid branch or tag ref.");
    }

    public static void ValidateSha(string? sha)
    {
        if (sha is null || !Regex.IsMatch(sha, "\\A[0-9a-fA-F]{40}\\z")) throw new ArgumentException("An exact SHA-1 object ID is required.");
    }

    private async Task<string> RunAsync(string? repository, string[] arguments, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null, string? input = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.OperationTimeoutSeconds));
        var start = new ProcessStartInfo(_options.GitExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment is not null)
            foreach (var value in environment) start.Environment[value.Key] = value.Value;
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"));
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("protocol.allow=never");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.longpaths=true");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("user.name=C-Sweet");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("user.email=csweet@localhost");
        if (repository is not null) { start.ArgumentList.Add("--git-dir"); start.ArgumentList.Add(repository); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        using var registration = timeout.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        // Cancel all process work immediately if either bounded reader fails.
        async Task<string> ReadAsync(StreamReader reader)
        {
            try { return await ReadBoundedAsync(reader, timeout.Token); }
            catch { await timeout.CancelAsync(); throw; }
        }
        async Task WriteInputAsync()
        {
            try
            {
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            catch { await timeout.CancelAsync(); throw; }
        }
        var output = ReadAsync(process.StandardOutput);
        var error = ReadAsync(process.StandardError);
        await Task.WhenAll(output, error, WriteInputAsync(), process.WaitForExitAsync(timeout.Token));
        if (process.ExitCode != 0) throw new InvalidOperationException("Git rejected the operation: " + error.Result.Trim());
        return output.Result;
    }

    private async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (builder.Length + count > _options.MaximumOutputBytes) throw new IOException("Git response exceeds the configured inspection limit.");
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }
}
