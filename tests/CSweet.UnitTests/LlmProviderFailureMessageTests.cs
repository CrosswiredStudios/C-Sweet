using CSweet.AgentHost.Broker;
namespace CSweet.UnitTests;
public sealed class LlmProviderFailureMessageTests
{
    [Fact]
    public void Missing_user_query_reports_the_compatibility_setting_without_raw_provider_details()
    {
        var message = LlmProviderFailureMessage.From(new InvalidOperationException("No user query found in messages. secret=private-value"));
        Assert.Contains("EnsureUserMessage", message);
        Assert.DoesNotContain("private-value", message);
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(400, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public void OnlyTemporaryProviderFailuresAreRetryable(int status, bool expected)
    {
        Assert.Equal(expected, LlmProviderFailureMessage.IsTransient(new HttpRequestException("provider",
            null, status == 0 ? null : (System.Net.HttpStatusCode)status)));
    }

    [Fact]
    public void TimeoutsAndUnloadedModelsRecoverButUnknownErrorsDoNot()
    {
        Assert.True(LlmProviderFailureMessage.IsTransient(new TimeoutException()));
        Assert.True(LlmProviderFailureMessage.IsTransient(new InvalidOperationException("No model loaded")));
        Assert.False(LlmProviderFailureMessage.IsTransient(new InvalidOperationException("Invalid request")));
    }
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
