using System.Runtime.CompilerServices;

namespace CSweet.UnitTests;

public sealed class GlobalUiAssetAndLifecycleTests
{
    [Fact]
    public void WebManifest_UsesTheFilenameRequestedByTheAppShell()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src", "CSweet.App", "wwwroot", "index.html"));

        Assert.Contains("/manifest.webmanifest", index, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "src", "CSweet.App", "wwwroot", "manifest.webmanifest")));
        Assert.False(File.Exists(Path.Combine(root, "src", "CSweet.App", "wwwroot", "manfiest.webmanifest")));
    }

    [Fact]
    public void GlobalComponents_DoNotReadCancellationTokenSourcesAfterAwaitingInitialization()
    {
        var components = Path.Combine(FindRepositoryRoot(), "src", "CSweet.UI", "Components");
        var capacityAlert = File.ReadAllText(Path.Combine(components, "ExecutionCapacityAlert.razor"));
        var realtimeCoordinator = File.ReadAllText(Path.Combine(components, "AppRealtimeCoordinator.razor"));

        Assert.Contains("var lifetimeToken = _lifetime.Token;", capacityAlert, StringComparison.Ordinal);
        Assert.Contains("PollAsync(lifetimeToken)", capacityAlert, StringComparison.Ordinal);
        Assert.DoesNotContain("PollAsync(_lifetime.Token)", capacityAlert, StringComparison.Ordinal);

        Assert.Contains("_disposeToken = _disposeCts.Token;", realtimeCoordinator, StringComparison.Ordinal);
        Assert.Contains("ConnectWithRetryAsync(_disposeToken)", realtimeCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectWithRetryAsync(_disposeCts.Token)", realtimeCoordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandCenter_TreatsMissingBriefingSettingsAsAnEmptyOptionalState()
    {
        var commandCenter = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "CSweet.UI", "Pages", "CommandCenter.razor"));

        Assert.Contains("response.StatusCode == System.Net.HttpStatusCode.NoContent", commandCenter, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
