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
        var routes = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Routes.razor"));
        Assert.DoesNotContain("<Navigating>", routes, StringComparison.Ordinal);
        Assert.Contains("OnNavigateAsync=\"GuardNavigationAsync\"", routes, StringComparison.Ordinal);
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

    [Fact]
    public void DirectReportsAreShownBeforeOtherAgents()
    {
        var razor = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "Communications.razor"));

        var reportsHeading = razor.IndexOf("<span>Direct Reports</span>", StringComparison.Ordinal);
        var agentsHeading = razor.IndexOf("<span>Agents</span>", StringComparison.Ordinal);
        Assert.True(reportsHeading >= 0);
        Assert.True(agentsHeading > reportsHeading);
        Assert.Contains("person.EmployeeType == \"Agent\"", razor, StringComparison.Ordinal);
        Assert.Contains("person.ReportsToOrganizationUserId == _hub?.CurrentOrganizationUserId", razor, StringComparison.Ordinal);
        Assert.Contains("@foreach (var entry in DirectReports)", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void HiringSuggestionCarouselSupportsCompactAccessibleHorizontalNavigation()
    {
        var root = FindRepositoryRoot();
        var component = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Components", "Hiring", "HiringSuggestionCarousel.razor"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Components", "Hiring", "HiringSuggestionCarousel.razor.css"));
        var script = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "wwwroot", "js", "communications.js"));

        Assert.Contains("Hiring suggestions", component, StringComparison.Ordinal);
        Assert.Contains("Previous hiring suggestion", component, StringComparison.Ordinal);
        Assert.Contains("Next hiring suggestion", component, StringComparison.Ordinal);
        Assert.Contains("else if (!ReadOnly)", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Hiring key", component, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overflow-x: auto", css, StringComparison.Ordinal);
        Assert.Contains("scroll-snap-type: x mandatory", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("scrollHiringCarousel", script, StringComparison.Ordinal);
    }

    [Fact]
    public void HiringSuggestionCarouselRendersCancelledAndSupersededStates()
    {
        var root = FindRepositoryRoot();
        var component = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Components", "Hiring", "HiringSuggestionCarousel.razor"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Components", "Hiring", "HiringSuggestionCarousel.razor.css"));

        Assert.Contains("SuggestedUserActionStatuses.Cancelled", component, StringComparison.Ordinal);
        Assert.Contains("SuggestedUserActionStatuses.Superseded", component, StringComparison.Ordinal);
        Assert.Contains("Replaced by @ReplacementRole(action)", component, StringComparison.Ordinal);
        Assert.Contains("This suggestion was cancelled", component, StringComparison.Ordinal);
        Assert.Contains("AllResolved", component, StringComparison.Ordinal);
        Assert.Contains(".hiring-suggestion-tile.superseded", css, StringComparison.Ordinal);
        Assert.Contains(".hiring-suggestion-tile.cancelled", css, StringComparison.Ordinal);
    }

    [Fact]
    public void CommunicationsShowsCancelledSuggestedActionCardWithoutButtons()
    {
        var root = FindRepositoryRoot();
        var razor = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Pages", "Communications.razor"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Pages", "Communications.razor.css"));

        Assert.Contains("SUGGESTED ACTION · CANCELLED", razor, StringComparison.Ordinal);
        Assert.Contains("hub-system-action-cancelled", razor, StringComparison.Ordinal);
        Assert.Contains("SuggestedUserActionStatuses.Superseded", razor, StringComparison.Ordinal);
        Assert.Contains(".hub-system-action-cancelled", css, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
