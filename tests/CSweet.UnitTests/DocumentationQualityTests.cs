using System.Text.RegularExpressions;

namespace CSweet.UnitTests;

public sealed partial class DocumentationQualityTests
{
    private static readonly string[] CanonicalDocuments =
    [
        "Documentation/Architecture/MCP_AGENT_RUNTIME.md",
        "Documentation/Security/AGENT_RUNTIME_THREAT_MODEL.md",
        "Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md",
        "Documentation/Operations/MCP_AGENT_RUNTIME_RUNBOOK.md"
    ];

    [Fact]
    public void CanonicalDocuments_HaveNoStaleLocalLinks()
    {
        var root = RepositoryRoot();
        var missing = new List<string>();
        foreach (var relativePath in CanonicalDocuments.Append("README.md"))
        {
            var documentPath = Path.Combine(root, relativePath);
            Assert.True(File.Exists(documentPath), $"Missing maintained document: {relativePath}");
            var directory = Path.GetDirectoryName(documentPath)!;
            foreach (Match match in MarkdownLink().Matches(File.ReadAllText(documentPath)))
            {
                var target = match.Groups["target"].Value.Trim('<', '>');
                if (target.StartsWith('#') ||
                    Uri.TryCreate(target, UriKind.Absolute, out _))
                    continue;
                target = target.Split('#', 2)[0].Replace('/', Path.DirectorySeparatorChar);
                if (target.Length > 0 && !File.Exists(Path.GetFullPath(Path.Combine(directory, target))))
                    missing.Add($"{relativePath} -> {target}");
            }
        }

        Assert.True(missing.Count == 0, "Stale documentation links:" + Environment.NewLine +
                                        string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void CurrentDocumentation_DoesNotDescribeLegacyTransportAsActive()
    {
        var root = RepositoryRoot();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(root, "Documentation/BROKERED_MCP_AND_HIRING.md"))
        };
        var historicalRoots = new[]
        {
            Path.GetFullPath(Path.Combine(root, "docs/implementation")),
            Path.GetFullPath(Path.Combine(root, "docs/analysis"))
        };
        var violations = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !excluded.Contains(Path.GetFullPath(path)) &&
                           historicalRoots.All(history => !Path.GetFullPath(path).StartsWith(history, StringComparison.OrdinalIgnoreCase)))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return ActiveLegacyTransport().IsMatch(text);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.True(violations.Length == 0,
            "Current documentation contains active legacy-transport wording:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"\[[^\]]+\]\((?<target>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(
        @"(?im)^\s*(?:[-*]\s+)?(?:agents?\s+(?:use|connect|invoke|communicate).{0,40}(?:gRPC|protobuf)|gRPC\s+is\s+the\s+(?:active|canonical)|active\s+(?:agent\s+)?protocol.{0,20}gRPC)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ActiveLegacyTransport();
}
