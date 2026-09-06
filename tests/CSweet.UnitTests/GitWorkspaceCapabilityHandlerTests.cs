using CSweet.AgentHost.Broker;
using CSweet.Agent.SDK;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class GitWorkspaceCapabilityHandlerTests
{
    [Theory]
    [InlineData(GitWorkspaceCapabilities.ListLocks)]
    [InlineData(GitWorkspaceCapabilities.LockFile)]
    [InlineData(GitWorkspaceCapabilities.UnlockFile)]
    public async Task FileLocksRequireAnExplicitCapabilityBeforeDatabaseOrHostAccess(string capability)
    {
        var handler = new GitWorkspaceCapabilityHandler(null!, new UnavailableTrustedGitHostClient(), null!, null!);
        var session = new AgentSession("session", "developer", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { GitWorkspaceCapabilities.Publish }, 1));
        var request = new RequestCapability { RequestId = "lock", Capability = capability,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { })) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.False(Assert.Single(results).Succeeded);
    }

    [Fact]
    public async Task UnconfiguredGitHostFailsClosedWithoutLocalFallback()
    {
        ITrustedGitHostClient client = new UnavailableTrustedGitHostClient();
        var request = new TrustedWorkspacePrepareRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "csweet/ticket",
            null,
            "prepare-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PrepareAsync(request, CancellationToken.None));

        Assert.Contains("blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without exposing credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
