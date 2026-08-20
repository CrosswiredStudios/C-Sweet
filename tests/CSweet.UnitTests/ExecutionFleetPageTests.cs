using System.Runtime.CompilerServices;

namespace CSweet.UnitTests;

public sealed class ExecutionFleetPageTests
{
    [Fact]
    public void OfflineOffice_ShowsReconnectProgressInsteadOfResumeAction()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "ExecutionFleet.razor"));

        Assert.Contains("node.Status == \"offline\"", source, StringComparison.Ordinal);
        Assert.Contains("This page checks for reconnection automatically.", source, StringComparison.Ordinal);
        Assert.Contains("<MudProgressLinear Indeterminate=\"true\"", source, StringComparison.Ordinal);
        Assert.Contains("else if (node.Status == \"draining\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("node.Status == \"draining\" || node.Status == \"offline\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeAction_DisplaysTheServerMutationMessage()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "ExecutionFleet.razor"));

        Assert.Contains("ReadFromJsonAsync<ExecutionFleetMutationResponse>()", source, StringComparison.Ordinal);
        Assert.Contains("_message = result?.Message", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
