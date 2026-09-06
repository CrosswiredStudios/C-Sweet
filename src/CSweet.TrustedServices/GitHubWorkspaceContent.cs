namespace CSweet.TrustedServices;

internal static class GitHubWorkspaceContent
{
    // Until GitHub LFS download/upload is supported, never expose pointer text as editable assets.
    public static async Task ValidateAsync(string directory, CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            await using var input = File.OpenRead(path);
            using var reader = new StreamReader(input);
            var prefix = new char[64];
            var count = await reader.ReadBlockAsync(prefix.AsMemory(), ct);
            if (new string(prefix, 0, count).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                throw new InvalidOperationException("GitHub LFS workspaces are not yet supported. No remote branch was changed.");
        }
    }
}
