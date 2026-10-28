using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class PluginConnectionAddressesTests
{
    [Fact]
    public void SeparateApiAndUiUseExactDeploymentConfiguredDestinations()
    {
        var options = new PluginConnectionOptions { PublicBaseUrl = "https://api.example.com", ReturnBaseUrl = "https://app.example.com" };
        Assert.Equal("https://api.example.com/api/plugin-connections/oauth/callback", PluginConnectionAddresses.Callback(options));
        Assert.Equal("https://app.example.com/settings/plugins/123?setup=456", PluginConnectionAddresses.Return(options, "/settings/plugins/123?setup=456"));
    }

    [Fact]
    public void SingleOriginDeploymentKeepsRelativeReturns()
    {
        Assert.Equal("/settings/plugins", PluginConnectionAddresses.Return(new(), "/settings/plugins"));
    }

    [Theory]
    [InlineData("http://localhost:5149")]
    [InlineData("https://user:secret@api.example.com")]
    [InlineData("https://api.example.com?callback=elsewhere")]
    [InlineData("https://api.example.com#fragment")]
    [InlineData("")]
    public void UnsafeCallbackBasesAreRejected(string address) =>
        Assert.Throws<InvalidOperationException>(() => PluginConnectionAddresses.Callback(new() { PublicBaseUrl = address }));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("//evil.example.com")]
    [InlineData("/\\evil.example.com")]
    public void ReturnPathsCannotSelectTheirOwnOrigin(string path) =>
        Assert.Throws<ArgumentException>(() => PluginConnectionAddresses.Return(new(), path));

    [Fact]
    public void HttpUiReturnIsOnlyAllowedOnLoopbackInDevelopment()
    {
        var options = new PluginConnectionOptions { ReturnBaseUrl = "http://localhost:5097" };
        Assert.Throws<InvalidOperationException>(() => PluginConnectionAddresses.Return(options, "/settings/plugins"));
        Assert.Equal("http://localhost:5097/settings/plugins", PluginConnectionAddresses.Return(options, "/settings/plugins", true));
        options.ReturnBaseUrl = "http://app.example.com";
        Assert.Throws<InvalidOperationException>(() => PluginConnectionAddresses.Return(options, "/settings/plugins", true));
    }
}
