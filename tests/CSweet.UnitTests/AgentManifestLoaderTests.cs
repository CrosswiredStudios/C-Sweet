using CSweet.Agent.SDK;

namespace CSweet.UnitTests;

public class AgentManifestLoaderTests
{
    [Fact]
    public async Task LoadAsync_ReadsDotNetProjectRuntimeFields()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"csweet-agent-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "manifestVersion": "2.0",
              "id": "com.example.research-agent",
              "name": "Research Agent",
              "version": "1.2.3",
              "publisher": { "id": "com.example", "name": "Example" },
              "runtime": {
                "type": "dotnet-project",
                "projectPath": "src/ResearchAgent/ResearchAgent.csproj",
                "targetFramework": "net10.0",
                "defaultActivationMode": "Periodic"
              },
              "protocol": { "minimumVersion": "2.0", "maximumVersion": "2.x" }
            }
            """);

        try
        {
            var manifest = await AgentManifestLoader.LoadAsync(manifestPath, CancellationToken.None);

            Assert.Equal("dotnet-project", manifest.Runtime.Type);
            Assert.Equal("src/ResearchAgent/ResearchAgent.csproj", manifest.Runtime.ProjectPath);
            Assert.Equal("net10.0", manifest.Runtime.TargetFramework);
            Assert.Equal("Periodic", manifest.Runtime.DefaultActivationMode);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }
}
