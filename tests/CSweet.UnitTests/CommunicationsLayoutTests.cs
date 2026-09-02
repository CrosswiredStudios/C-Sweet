using System.Runtime.CompilerServices;

namespace CSweet.UnitTests;

public sealed class CommunicationsLayoutTests
{
    [Fact]
    public void DocumentLayoutBoundsTheNestedMessageScroller()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "wwwroot", "css", "app.css"));

        Assert.Contains(
            ".communications-document-layout { display: flex; min-width: 0; min-height: 0; height: 100%; overflow: hidden; }",
            css,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
