namespace CSweet.TrustedServices;

internal static class GitHubWorkspaceContent
{
    // Agent snapshots must contain actual assets, never unresolved pointers that could be republished as file data.
    public static async Task ValidateAsync(string directory, CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            await using var input = File.OpenRead(path);
            using var reader = new StreamReader(input);
            var prefix = new char[64];
            var count = await reader.ReadBlockAsync(prefix.AsMemory(), ct);
            if (new string(prefix, 0, count).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                throw new InvalidOperationException("GitHub workspace contains unresolved LFS pointers. Prepare the original assets before publishing.");
        }
    }
}
