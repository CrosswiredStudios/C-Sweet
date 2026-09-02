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

    [Fact]
    public void CommunicationsDocumentWorkspaceSlidesInFromTheRight()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "wwwroot", "css", "app.css"));

        Assert.Contains(
            ".communications-document-layout > .artifact-workspace { animation: communications-artifact-enter",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "from { opacity: 0; transform: translateX(100%); }",
            css,
            StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
    }


    [Fact]
    public void CommunicationsRouteChangesKeepTheWorkspaceMounted()
    {
        var razor = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "Communications.razor"));

        Assert.DoesNotContain(
            "protected override Task OnParametersSetAsync() => LoadAsync();",
            razor,
            StringComparison.Ordinal);
        Assert.Contains(
            "await SwitchChatAsync(chat, updateLocation: false);",
            razor,
            StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(CurrentWorkspaceHref(artifactId));", razor, StringComparison.Ordinal);
    }
    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
