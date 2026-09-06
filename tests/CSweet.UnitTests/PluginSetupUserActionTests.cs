using System.Text.Json;
using CSweet.Infrastructure.Communications;

namespace CSweet.UnitTests;

public sealed class PluginSetupUserActionTests
{
    [Fact]
    public void SetupButtonAlwaysUsesTheAuthenticatedInstallation()
    {
        var org = Guid.NewGuid(); var installation = Guid.NewGuid();
        var result = new PluginSetupUserActionWorkflowResolver().Resolve(org, installation, JsonSerializer.SerializeToElement(new { }));
        Assert.Equal($"/organizations/{org:D}/plugin-setup/{installation:D}", result.NavigationUri);
    }

    [Theory]
    [InlineData("{\"url\":\"https://attacker.example\"}")]
    [InlineData("{\"installationId\":\"00000000-0000-0000-0000-000000000002\"}")]
    [InlineData("[]")]
    public void APluginCannotChooseRedirectsOrAnotherInstallation(string json) => Assert.Throws<ArgumentException>(() =>
        new PluginSetupUserActionWorkflowResolver().Resolve(Guid.NewGuid(), Guid.NewGuid(), JsonDocument.Parse(json).RootElement));
}
