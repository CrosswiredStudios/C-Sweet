using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.Compute.Contracts;

public sealed record ComputeBrowserTest(Guid RequestId, string SourceCommitSha, string BuildSha256,
    IReadOnlyList<int> ViewportWidths, bool RequireWebGl = true)
{
    public const string Feature = "browser-testing-v1";
    public const string Image = "csweet/browser-tests:1.64.0";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory => "/var/lib/csweet-compute/work/browser-" + BuildSha256;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContainerName => "csweet-browser-" + RequestId.ToString("N");

    public ComputeBrowserTest Validate()
    {
        if (RequestId == Guid.Empty || !Regex.IsMatch(SourceCommitSha ?? "", "\\A[a-f0-9]{40}([a-f0-9]{24})?\\z") ||
            !Regex.IsMatch(BuildSha256 ?? "", "\\A[a-f0-9]{64}\\z") || ViewportWidths is not { Count: >= 1 and <= 4 } ||
            ViewportWidths.Any(width => width is < 240 or > 3840) || ViewportWidths.Distinct().Count() != ViewportWidths.Count)
            throw new ArgumentException("Browser tests need an exact commit, build SHA-256 and one to four distinct viewport widths.");
        return this with { ViewportWidths = ViewportWidths.ToArray() };
    }

    public ComputeGuestCommand Command() => new(RequestId, "/usr/bin/docker", "/var/lib/csweet-compute/work",
        ["run", "--name", ContainerName, "--init", "--network=none", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges",
         "--memory=1536m", "--cpus=2", "--pids-limit=256", "--shm-size=256m", "--tmpfs=/tmp:rw,size=256m",
         "--mount", $"type=bind,src={Directory},dst=/input,readonly",
         "--mount", $"type=bind,src={Directory}/evidence,dst=/evidence", Image,
         JsonSerializer.Serialize(Validate(), ComputeProtocol.Json)], 30, 8192);
}
