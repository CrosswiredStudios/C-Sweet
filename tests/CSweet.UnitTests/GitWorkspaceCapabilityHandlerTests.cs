using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class GitWorkspaceCapabilityHandlerTests
{
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
