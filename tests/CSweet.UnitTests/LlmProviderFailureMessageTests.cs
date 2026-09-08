using CSweet.AgentHost.Broker;
namespace CSweet.UnitTests;
public sealed class LlmProviderFailureMessageTests
{
    [Fact]
    public void MissingModelHasActionableMessageWithoutEchoingProviderBody()
    {
        var message = LlmProviderFailureMessage.From(new InvalidOperationException("HTTP 400 No model loaded. secret=private-value"));
        Assert.Contains("Load the configured model", message);
        Assert.DoesNotContain("private-value", message);
    }
    [Fact]
    public void UnknownProviderFailuresDoNotExposeProviderText()
    {
        Assert.DoesNotContain("private-value", LlmProviderFailureMessage.From(new InvalidOperationException("private-value")));
    }
}
