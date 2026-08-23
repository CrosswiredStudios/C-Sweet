using System.Text.Json;
using CSweet.Api.Chat;

namespace CSweet.UnitTests;

public sealed class ChatTraceSanitizerTests
{
    [Fact]
    public void Recursively_redacts_secrets_protected_reasoning_and_restricted_memory()
    {
        var sanitized = ChatTraceSanitizer.SanitizeDetails(new
        {
            request = new
            {
                authorization = "Bearer visible-secret",
                nested = new
                {
                    apiKey = "sk-test",
                    protectedReasoning = "opaque-provider-payload",
                    restrictedMemory = "private memory",
                    ordinary = "safe"
                }
            }
        });

        var json = JsonSerializer.Serialize(sanitized);
        Assert.DoesNotContain("visible-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-provider-payload", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private memory", json, StringComparison.Ordinal);
        Assert.Contains("safe", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def", "Authorization: [REDACTED]")]
    [InlineData("password=hunter2", "password=[REDACTED]")]
    [InlineData("cookie: session-value", "cookie: [REDACTED]")]
    public void Redacts_sensitive_text(string input, string expected) =>
        Assert.Equal(expected, ChatTraceSanitizer.SanitizeText(input));
}
