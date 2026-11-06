using System.Text;

namespace CSweet.UnitTests;

public sealed class SourceEncodingTests
{
    // One stray Windows-1252 byte makes the compiler read the whole file in the legacy code page,
    // so every correct UTF-8 character in it renders garbled (a middle dot shows as "Â·").
    private static readonly string[] Extensions = [".razor", ".cs", ".cshtml", ".css", ".js", ".json", ".html"];

    [Fact]
    public void EverySourceFileIsValidUtf8()
    {
        var root = FindRepositoryRoot();
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var invalid = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules" or "lib"))
                continue;
            try { strict.GetString(File.ReadAllBytes(file)); }
            catch (DecoderFallbackException) { invalid.Add(relative); }
        }
        Assert.True(invalid.Count == 0, "Not valid UTF-8: " + string.Join(", ", invalid));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
