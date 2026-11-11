using System.Diagnostics;
using System.Text;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public const int MaximumGitRequestBytes = 128 * 1024 * 1024;
    public const int MaximumGitResponseBytes = 256 * 1024 * 1024;

    public async Task<InternalGitHttpResponse> ExchangeAsync(InternalGitHttpRequest request, CancellationToken ct = default)
    {
        if (request.Service is not ("git-upload-pack" or "git-receive-pack") || request.Body.Length > MaximumGitRequestBytes || request.ProtectedBranches.Count > 10000)
            throw new ArgumentException("Unsupported Git request.");
        foreach (var branch in request.ProtectedBranches) ValidateBranch(branch);
        if (request.AgentBranch is not null)
        {
            ValidateBranch(request.AgentBranch);
            ValidateSha(request.AgentBaseCommitSha);
            if (request.AgentMaximumBlobBytes < 1) throw new ArgumentException("Unsupported Git request.");
        }
        else if (request.AgentBaseCommitSha is not null || request.AgentMaximumBlobBytes != 0)
            throw new ArgumentException("Unsupported Git request.");
        using var businessLease = EnterBusinessStorage(request.OrganizationId);
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hooks = Path.Combine(repository, "csweet-http-hooks");
        Directory.CreateDirectory(hooks);
        var protectedRefs = Path.Combine(hooks, "protected-refs");
        var protectedBranches = request.ProtectedBranches.Concat(await ManagedBranchesAsync(repository, ct)).Distinct();
        await File.WriteAllTextAsync(protectedRefs, string.Join("\n", protectedBranches.Select(b => "refs/heads/" + b)) + "\n", new UTF8Encoding(false), ct);
        var lockedPaths = Path.Combine(hooks, "locked-paths");
        var locks = request.Service == "git-receive-pack" && !request.Advertise ? await ReadFileLocksAsync(repository, ct) : [];
        await File.WriteAllTextAsync(lockedPaths, string.Join("\n", locks.Where(l => l.OwnerId != request.ActorId).Select(l => l.Path)) + "\n", new UTF8Encoding(false), ct);
        var hook = Path.Combine(hooks, "pre-receive");
        // This is trusted host code, never sourced from a repository tree. Values enter through a data file.
        const string script = "#!/bin/sh\nzero=0000000000000000000000000000000000000000\nwhile read old new ref; do\n case \"$ref\" in refs/heads/*|refs/tags/*) ;; *) echo 'Unsupported ref namespace' >&2; exit 1 ;; esac\n if [ -n \"$CSWEET_AGENT_REF\" ]; then\n  if [ \"$ref\" != \"$CSWEET_AGENT_REF\" ]; then echo 'C-Sweet accepts agent pushes only to the assigned work branch' >&2; exit 1; fi\n  if [ \"$new\" = \"$zero\" ]; then echo 'C-Sweet does not let agents delete branches' >&2; exit 1; fi\n  if ! \"$CSWEET_GIT_EXECUTABLE\" merge-base --is-ancestor \"$CSWEET_AGENT_BASE\" \"$new\"; then echo 'C-Sweet requires the work branch to descend from its assigned base commit' >&2; exit 1; fi\n  first=\"$old\"\n  if [ \"$old\" = \"$zero\" ]; then first=\"$CSWEET_AGENT_BASE\"; fi\n  changed=$(\"$CSWEET_GIT_EXECUTABLE\" -c core.longpaths=true diff-tree -r --no-renames --diff-filter=AMT \"$first\" \"$new\") || exit 1\n  link=$(printf '%s\\n' \"$changed\" | while read before after rest; do case \"$after\" in 120000|160000) echo found; break ;; esac; done)\n  if [ -n \"$link\" ]; then echo 'C-Sweet rejects symbolic links and submodules' >&2; exit 1; fi\n  objects=$(\"$CSWEET_GIT_EXECUTABLE\" rev-list --objects \"$first..$new\") || exit 1\n  sizes=$(printf '%s\\n' \"$objects\" | \"$CSWEET_GIT_EXECUTABLE\" cat-file --batch-check='%(objecttype) %(objectsize) %(rest)') || exit 1\n  large=$(printf '%s\\n' \"$sizes\" | while read type size path; do if [ \"$type\" = blob ] && [ \"$size\" -gt \"$CSWEET_AGENT_MAXIMUM_BLOB_BYTES\" ]; then printf '%s\\n' \"$path\"; break; fi; done)\n  if [ -n \"$large\" ]; then echo \"C-Sweet requires Git LFS for files over the size limit: $large\" >&2; exit 1; fi\n else\n  while IFS= read -r protected; do\n   if [ \"$ref\" = \"$protected\" ]; then echo 'C-Sweet protects this branch' >&2; exit 1; fi\n  done < \"$CSWEET_PROTECTED_REFS\"\n fi\n while IFS= read -r locked; do\n  [ -n \"$locked\" ] || continue\n  before=\"$old\"\n  after=\"$new\"\n  if [ \"$old\" = \"$zero\" ]; then\n   before=$(\"$CSWEET_GIT_EXECUTABLE\" merge-base HEAD \"$new\" 2>/dev/null) || before=$(\"$CSWEET_GIT_EXECUTABLE\" hash-object -t tree --stdin </dev/null) || exit 1\n  fi\n  if [ \"$new\" = \"$zero\" ]; then\n   after=$(\"$CSWEET_GIT_EXECUTABLE\" hash-object -t tree --stdin </dev/null) || exit 1\n  fi\n  if ! \"$CSWEET_GIT_EXECUTABLE\" -c core.longpaths=true diff --quiet --no-ext-diff --no-textconv --no-renames \"$before\" \"$after\" -- \":(literal)$locked\"; then\n   echo \"C-Sweet rejects changes to locked file: $locked\" >&2\n   exit 1\n  fi\n done < \"$CSWEET_LOCKED_PATHS\"\ndone\nexit 0\n";
        await File.WriteAllTextAsync(hook, script, new UTF8Encoding(false), ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(_options.GitExecutable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["CSWEET_GIT_EXECUTABLE"] = _options.GitExecutable.Replace('\\', '/');
        start.Environment["CSWEET_LOCKED_PATHS"] = lockedPaths.Replace('\\', '/');
        start.Environment["CSWEET_PROTECTED_REFS"] = protectedRefs.Replace('\\', '/');
        foreach (var key in new[] { "CSWEET_AGENT_REF", "CSWEET_AGENT_BASE", "CSWEET_AGENT_MAXIMUM_BLOB_BYTES" }) start.Environment.Remove(key);
        if (request.AgentBranch is not null)
        {
            // Validated scalars reach the hook as data. They are never interpolated into its text.
            start.Environment["CSWEET_AGENT_REF"] = "refs/heads/" + request.AgentBranch;
            start.Environment["CSWEET_AGENT_BASE"] = request.AgentBaseCommitSha!.ToLowerInvariant();
            start.Environment["CSWEET_AGENT_MAXIMUM_BLOB_BYTES"] = request.AgentMaximumBlobBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var config in new[] { "core.hooksPath=" + hooks.Replace('\\', '/'), "core.longpaths=true", "protocol.allow=never",
            "uploadpack.hideRefs=refs/csweet", "receive.hideRefs=refs/csweet", "receive.fsckObjects=true", "receive.denyNonFastForwards=true", "receive.advertiseAtomic=true" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add(config); }
        start.ArgumentList.Add(request.Service[4..]); start.ArgumentList.Add("--stateless-rpc");
        if (request.Advertise) start.ArgumentList.Add("--advertise-refs");
        start.ArgumentList.Add(repository);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.OperationTimeoutSeconds));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        using var output = new MemoryStream();
        if (request.Advertise)
        {
            var service = Encoding.ASCII.GetBytes($"# service={request.Service}\n");
            output.Write(Encoding.ASCII.GetBytes((service.Length + 4).ToString("x4"))); output.Write(service); output.Write("0000"u8);
        }
        async Task ReadAsync()
        {
            try
            {
                var buffer = new byte[65536]; int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (output.Length + count > MaximumGitResponseBytes) throw new IOException("Git transfer exceeds the response limit.");
                    output.Write(buffer, 0, count);
                }
            }
            catch { await timeout.CancelAsync(); throw; }
        }
        async Task WriteAsync()
        {
            try { await process.StandardInput.BaseStream.WriteAsync(request.Body, timeout.Token); process.StandardInput.Close(); }
            catch { await timeout.CancelAsync(); throw; }
        }
        async Task<string> ErrorAsync()
        { try { return await ReadBoundedAsync(process.StandardError, timeout.Token); } catch { await timeout.CancelAsync(); throw; } }
        var error = ErrorAsync();
        await Task.WhenAll(ReadAsync(), WriteAsync(), error, process.WaitForExitAsync(timeout.Token));
        if (process.ExitCode != 0) throw new InvalidOperationException("Git rejected the transfer.");
        return new($"application/x-{request.Service}-{(request.Advertise ? "advertisement" : "result")}", output.ToArray());
    }
}
