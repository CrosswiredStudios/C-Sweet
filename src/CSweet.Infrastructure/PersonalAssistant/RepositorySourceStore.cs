using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.SourceControl;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Domain.PersonalAssistant;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.PersonalAssistant;

public sealed class RepositoryOnboardingOptions
{
    public const string SectionName = "CSweet:RepositoryOnboarding";
    public string StagingPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "repository-onboarding");
    public long MaximumUploadBytes { get; set; } = 512L * 1024 * 1024;
}

/// <summary>Validates complete archives; only a bounded, redacted text selection reaches an agent.</summary>
public sealed class RepositorySourceStore(IOptions<RepositoryOnboardingOptions> options,
    ITrustedSourceControlHostClient host) : IRepositorySourceStore
{
    private static readonly WorkspaceArtifactValidator Validator = new();
    private static readonly Regex SecretLine = new(@"(?im)^.*(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*[^\r\n]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "node_modules", "bin", "obj", "dist", "build", "vendor", ".godot", ".venv", "venv", "Library", "Temp", "Logs", ".next", "coverage" };

    public Task<StagedRepositorySource> StageUploadAsync(Guid user, Guid draft, Guid sourceId,
        string name, Stream archive, CancellationToken token) => StageAsync(user, draft, sourceId, name, archive, true, token);

    private async Task<StagedRepositorySource> StageAsync(Guid user, Guid draft, Guid sourceId,
        string name, Stream archive, bool unwrap, CancellationToken token)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Upload a repository ZIP file.");
        var temp = Path.Combine(Root(user, draft), "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var incoming = Path.Combine(temp, "incoming.zip");
            await using (var output = File.Create(incoming))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await archive.ReadAsync(buffer, token)) > 0)
                {
                    total += count; if (total > options.Value.MaximumUploadBytes) throw new InvalidDataException("The ZIP exceeds the 512 MiB upload limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            var extracted = Path.Combine(temp, "files");
            await using (var input = File.OpenRead(incoming)) await Validator.ExtractZipAsync(input, extracted, token);
            var entries = Directory.GetFileSystemEntries(extracted);
            var contentRoot = unwrap && entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : extracted;
            if (!Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).Any()) throw new InvalidDataException("The ZIP contains no repository files.");
            var normalized = Path.Combine(temp, "normalized.zip"); WorkspaceArtifactManifest manifest;
            await using (var output = File.Create(normalized)) manifest = await Validator.CreateZipAsync(contentRoot, output, token);
            await using var normalizedInput = File.OpenRead(normalized);
            var key = Convert.ToHexString(await SHA256.HashDataAsync(normalizedInput, token)).ToLowerInvariant();
            var target = ArchivePath(user, draft, key);
            if (!File.Exists(target)) File.Copy(normalized, target);
            var display = Path.GetFileNameWithoutExtension(name);
            if (display.Length > 160) display = display[..160];
            return new(display, key, manifest.Sha256, await SelectTextAsync(contentRoot, sourceId, display, manifest.Sha256, token));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    public async Task<StagedRepositorySource> PrepareGitHubAsync(Guid user, Guid draft,
        RepositoryOnboardingSource source, CancellationToken token)
    {
        var snapshot = await host.PrepareOnboardingSnapshotAsync(new(
            source.InstallationId!.Value, source.ExternalRepositoryId!.Value, source.Owner, source.Name,
            source.DefaultBranch, source.Id, source.DefaultBranch, source.Snapshot,
            $"onboarding:{draft:N}:{source.Id:N}"), token);
        using var archive = new MemoryStream(snapshot.Archive, false);
        var staged = await StageAsync(user, draft, source.Id, source.Name + ".zip", archive, false, token);
        return staged with
        {
            Snapshot = snapshot.BaseCommitSha,
            Input = staged.Input with
            { Name = source.Owner + "/" + source.Name, Snapshot = snapshot.BaseCommitSha }
        };
    }

    public Task<Stream> OpenAsync(Guid user, Guid draft, string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(ArchivePath(user, draft, key), FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task DiscardAsync(Guid user, Guid draft, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Root(user, draft);
        // Root() has already checked its resolved path remains inside the configured store.
        if (Directory.Exists(target)) Directory.Delete(target, true);
        return Task.CompletedTask;
    }

    private string Root(Guid user, Guid draft)
    {
        if (user == Guid.Empty || draft == Guid.Empty || !Path.IsPathFullyQualified(options.Value.StagingPath))
            throw new InvalidOperationException("A user-owned staging directory is required.");
        var root = Path.GetFullPath(options.Value.StagingPath);
        var path = Path.GetFullPath(Path.Combine(root, user.ToString("N"), draft.ToString("N")));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The staging path is outside its store.");
        Directory.CreateDirectory(path);
        return path;
    }
    private string ArchivePath(Guid user, Guid draft, string key)
    {
        if (key.Length != 64 || key.Any(x => !Uri.IsHexDigit(x))) throw new ArgumentException("The staged snapshot identity is invalid.");
        return Path.Combine(Root(user, draft), key.ToLowerInvariant() + ".zip");
    }

    internal static async Task<RepositoryAnalysisInput> SelectTextAsync(string root, Guid sourceId, string name,
        string snapshot, CancellationToken token)
    {
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => new { Full = p, Path = Path.GetRelativePath(root, p).Replace('\\', '/') })
            .Where(x => !x.Path.Split('/').Any(IgnoredDirectories.Contains) && IsTextCandidate(x.Path))
            .OrderBy(x => Path.GetFileName(x.Path).StartsWith("README", StringComparison.OrdinalIgnoreCase) ? 0 : x.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
        var files = new List<RepositorySourceFile>(); var warnings = new List<string>(); var budget = 24000;
        foreach (var file in candidates.Take(80))
        {
            if (budget <= 0) break;
            await using var stream = File.OpenRead(file.Full);
            var bytes = new byte[(int)Math.Min(stream.Length, Math.Min(8192, budget))];
            var read = await stream.ReadAsync(bytes, token);
            if (bytes.AsSpan(0, read).Contains((byte)0)) continue;
            var content = Encoding.UTF8.GetString(bytes, 0, read);
            if (content.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) { warnings.Add($"Excluded sensitive content in {file.Path}."); continue; }
            content = SecretLine.Replace(content, "[credential assignment omitted]");
            files.Add(new(file.Path, content)); budget -= content.Length;
            if (stream.Length > read) warnings.Add($"Read a bounded excerpt of {file.Path}.");
        }
        if (candidates.Length > files.Count) warnings.Add("Some documentation was omitted to keep the analysis bounded.");
        warnings.Add("Binary assets, generated files, credential files and dependencies were not sent to the assistant. Builds were not run; submodules were not fetched.");
        return new(sourceId.ToString("D"), name, snapshot, files, warnings);
    }

    private static bool IsTextCandidate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt" or ".csproj" or ".slnx" or ".gd" or ".ts" or ".tsx" or ".cs" ||
            name is "project.godot" or "package.json" or "Cargo.toml" or "pyproject.toml" or "requirements.txt" or "CMakeLists.txt";
    }
}
