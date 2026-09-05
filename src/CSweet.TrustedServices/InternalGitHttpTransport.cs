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
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hooks = Path.Combine(repository, "csweet-http-hooks");
        Directory.CreateDirectory(hooks);
        var protectedRefs = Path.Combine(hooks, "protected-refs");
        await File.WriteAllTextAsync(protectedRefs, string.Join("\n", request.ProtectedBranches.Select(b => "refs/heads/" + b)) + "\n", new UTF8Encoding(false), ct);
        var lockedPaths = Path.Combine(hooks, "locked-paths");
        var locks = request.Service == "git-receive-pack" && !request.Advertise ? await ReadFileLocksAsync(repository, ct) : [];
        await File.WriteAllTextAsync(lockedPaths, string.Join("\n", locks.Where(l => l.OwnerId != request.ActorId).Select(l => l.Path)) + "\n", new UTF8Encoding(false), ct);
        var hook = Path.Combine(hooks, "pre-receive");
        // This is trusted host code, never sourced from a repository tree. Values enter through a data file.
        const string script = "#!/bin/sh\nzero=0000000000000000000000000000000000000000\nwhile read old new ref; do\n case \"$ref\" in refs/heads/*|refs/tags/*) ;; *) echo 'Unsupported ref namespace' >&2; exit 1 ;; esac\n while IFS= read -r protected; do\n  if [ \"$ref\" = \"$protected\" ]; then echo 'C-Sweet protects this branch' >&2; exit 1; fi\n done < \"$CSWEET_PROTECTED_REFS\"\n while IFS= read -r locked; do\n  [ -n \"$locked\" ] || continue\n  before=\"$old\"\n  after=\"$new\"\n  if [ \"$old\" = \"$zero\" ]; then\n   before=$(\"$CSWEET_GIT_EXECUTABLE\" merge-base HEAD \"$new\" 2>/dev/null) || before=$(\"$CSWEET_GIT_EXECUTABLE\" hash-object -t tree --stdin </dev/null) || exit 1\n  fi\n  if [ \"$new\" = \"$zero\" ]; then\n   after=$(\"$CSWEET_GIT_EXECUTABLE\" hash-object -t tree --stdin </dev/null) || exit 1\n  fi\n  if ! \"$CSWEET_GIT_EXECUTABLE\" -c core.longpaths=true diff --quiet --no-ext-diff --no-textconv --no-renames \"$before\" \"$after\" -- \":(literal)$locked\"; then\n   echo \"C-Sweet rejects changes to locked file: $locked\" >&2\n   exit 1\n  fi\n done < \"$CSWEET_LOCKED_PATHS\"\ndone\nexit 0\n";
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
