namespace CSweet.UnitTests;

public sealed class SatelliteOfficeExtractionBoundaryTests
{
    private static readonly string[] MigratedProjectNames =
    [
        "CSweet.AgentRuntime",
        "CSweet.ExecutionNode",
        "CSweet.RuntimeHost",
    ];

    [Fact]
    public void Core_solution_does_not_reference_migrated_satellite_office_projects()
    {
        var root = FindRepositoryRoot();
        var definitionFiles = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "CSweet.slnx"))
            .Append(Path.Combine(root, "CSweet.sln"));

        foreach (var file in definitionFiles)
        {
            var contents = File.ReadAllText(file);
            foreach (var migratedProjectName in MigratedProjectNames)
            {
                Assert.DoesNotContain(migratedProjectName, contents, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.False(Directory.Exists(Path.Combine(root, "src", "CSweet.ExecutionNode")));
        Assert.False(Directory.Exists(Path.Combine(root, "src", "CSweet.RuntimeHost")));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(root, "src"),
            "CSweet.AgentRuntime.*",
            SearchOption.TopDirectoryOnly));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the C-Sweet repository root.");
    }
}
