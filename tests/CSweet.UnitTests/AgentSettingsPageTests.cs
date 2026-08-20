using System.Runtime.CompilerServices;

namespace CSweet.UnitTests;

public sealed class AgentSettingsPageTests
{
    [Fact]
    public void InstalledDefinitions_ExposeUpdateAndRemoveActionsAndStartTheUpdateCheck()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "Agents.razor"));

        Assert.Contains("StartUpdateCheck();", source, StringComparison.Ordinal);
        Assert.Contains("AgentApi.CheckDefinitionUpdatesAsync", source, StringComparison.Ordinal);
        Assert.Contains("OpenUpdateDialog(installation, availableUpdate)", source, StringComparison.Ordinal);
        Assert.Contains("OpenRemoveDialog(installation)", source, StringComparison.Ordinal);
        Assert.Contains("AgentApi.UpdateDefinitionAsync", source, StringComparison.Ordinal);
        Assert.Contains("AgentApi.RemoveDefinitionAsync", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
