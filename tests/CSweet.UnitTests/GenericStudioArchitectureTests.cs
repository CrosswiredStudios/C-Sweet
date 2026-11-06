namespace CSweet.UnitTests;

public sealed class GenericStudioArchitectureTests
{
    [Fact]
    public void CoreImplementationContainsNoVideoGameOrEngineBranches()
    {
        var root = RepositoryRoot();
        var sourceRoots = new[]
        {
            "CSweet.Api", "CSweet.Application", "CSweet.Domain", "CSweet.Infrastructure",
            "CSweet.AgentHost", "CSweet.Contracts"
        };
        var forbidden = new[] { "phaser", "babylon", "godot", "video-game-" };
        // Exact reviewed snippets, removed before scanning so any other vertical reference in
        // the same file still fails.
        var exceptions = new Dictionary<string, string[]>
        {
            // Ecosystem marker data alongside Cargo.toml, package.json and similar, not a branch.
            ["src/CSweet.Infrastructure/PersonalAssistant/RepositorySourceStore.cs"] = ["\".godot\"", "\"project.godot\""],
            // Verbatim persisted failure text matched to recover legacy blocked deliveries.
            ["src/CSweet.Infrastructure/WorkManagement/WorkDeliveryService.ComputeRecovery.cs"] = ["node_modules/phaser/package.json"],
            // Known debt: shared studio-team access is keyed to the game production profile.
            // Move this rule into profile data so the core stays vertical-neutral.
            ["src/CSweet.Infrastructure/Core/ProjectSetupService.GamePortfolio.cs"] = ["\"video-game-production.v2\""],
        };
        var violations = sourceRoots
            .SelectMany(name => Directory.EnumerateFiles(Path.Combine(root, "src", name), "*.cs",
                SearchOption.AllDirectories))
            .Select(file => new { File = Path.GetRelativePath(root, file).Replace('\\', '/'), Text = File.ReadAllText(file) })
            .Select(file => new { file.File, Text = exceptions.GetValueOrDefault(file.File, [])
                .Aggregate(file.Text, (text, allowed) => text.Replace(allowed, "", StringComparison.Ordinal)) })
            .Where(file => forbidden.Any(value => file.Text.Contains(value, StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.File)
            .ToList();

        Assert.True(violations.Count == 0,
            $"Vertical branches belong in profiles, contracts, agents, or adapters: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ProjectInspectionUiExposesRequiredRoutesFiltersDeepLinksAndLiveRefresh()
    {
        var root = RepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "CSweet.UI", "Pages", "Projects.razor"));
        var endpoints = File.ReadAllText(Path.Combine(root, "src", "CSweet.Api", "Core",
            "WorkstreamInspectionEndpoints.cs"));
        var navigation = File.ReadAllText(Path.Combine(root, "src", "CSweet.UI", "Layout", "NavMenu.razor"));

        Assert.Contains("/organizations/{OrganizationId:guid}/projects", page, StringComparison.Ordinal);
        Assert.Contains("/organizations/{OrganizationId:guid}/projects/{WorkstreamId:guid}", page, StringComparison.Ordinal);
        Assert.All(new[] { "Profile", "Phase", "Manager", "Health", "Team", "Release readiness",
            "Actor", "Resource or event", "Correlation or causation", "Outcome" },
            label => LocalizedSource.AssertContains(label, page));
        Assert.Contains("Realtime.EventReceived += OnRealtimeEvent", page, StringComparison.Ordinal);
        var inspectionPanels = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "src", "CSweet.UI", "Components", "Projects"), "*.razor").Select(File.ReadAllText));
        Assert.Contains(".DeepLink", page + inspectionPanels, StringComparison.Ordinal);
        Assert.Contains("Projects", navigation, StringComparison.Ordinal);
        Assert.All(new[] { "/work/boards/", "/documents?artifact=", "/communications/", "?tab=governance", "?tab=evidence" },
            link => Assert.Contains(link, endpoints, StringComparison.Ordinal));
        Assert.Contains("TeamMemberships", endpoints, StringComparison.Ordinal);
        Assert.Contains("WorkstreamSupervisionAssignments", endpoints, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "CSweet.Api")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "CSweet.UnitTests")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the C-Sweet repository root.");
    }
}
