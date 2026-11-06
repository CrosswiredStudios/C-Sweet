namespace CSweet.UnitTests;

/// <summary>
/// Runs a test only when the named agent repositories (<c>CSweet.Agent.{name}</c>) are checked out
/// beside an ancestor of the test output, as in a full Laboratory workspace. CI and single-repository
/// clones skip these cross-repository contract checks instead of failing on missing files.
/// </summary>
public sealed class SiblingAgentRepositoryFactAttribute : FactAttribute
{
    public SiblingAgentRepositoryFactAttribute(params string[] agents)
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
            if (agents.All(agent => Directory.Exists(Path.Combine(root.FullName, "CSweet.Agent." + agent))))
                return;
        Skip = $"Requires sibling agent repositories: {string.Join(", ", agents.Select(agent => "CSweet.Agent." + agent))}.";
    }
}
