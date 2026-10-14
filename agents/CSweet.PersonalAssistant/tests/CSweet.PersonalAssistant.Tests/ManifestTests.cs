using CSweet.Agent.SDK;

namespace CSweet.PersonalAssistant.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Manifest_IsValidAndMatchesAgent()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        Assert.Equal("com.csweet.personal-assistant", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Contains(PersonalAssistantAgent.ConverseCapability, manifest.Capabilities);
        Assert.Contains(PersonalAssistantAgent.AnalyzeCapability, manifest.Capabilities);
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               (!File.Exists(Path.Combine(directory.FullName, "csweet-plugin.json")) ||
                !Directory.Exists(Path.Combine(directory.FullName, "src"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
