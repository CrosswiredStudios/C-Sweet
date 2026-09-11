using System.Runtime.CompilerServices;

namespace CSweet.UnitTests;

public sealed class ExecutionFleetPageTests
{
    [Fact]
    public void OfflineOffice_ExplainsReconnectInsteadOfOfferingResume()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "ExecutionFleet.razor"));

        Assert.Contains("Status(node) == \"Offline\"", source, StringComparison.Ordinal);
        Assert.Contains("Open Office care to get it working again", source, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("https://downloads.test/office.msi", "x64", true)]
    [InlineData("http://downloads.test/office.msi", "x64", false)]
    [InlineData("https://downloads.test/office.msi", "arm64", false)]
    [InlineData("https://user@downloads.test/office.msi", "x64", false)]
    public void RecoveryDownloadMatchesTheMachineAndRequiresHttps(string url, string architecture, bool expected)
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {
            schemaVersion = 1, assets = new[] { new { operatingSystem = "windows", architecture, packageType = "msi", url, sha256 = new string('a', 64) } }
        }));
        var result = CSweet.Api.Setup.ExecutionFleetEndpoints.FindRecoveryPackage(manifest.RootElement, "windows", "x64");
        Assert.Equal(expected, result is not null);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
