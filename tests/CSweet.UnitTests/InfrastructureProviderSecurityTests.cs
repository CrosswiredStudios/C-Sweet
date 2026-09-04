using System.Text.Json.Nodes;
using CSweet.Infrastructure.Infrastructure;

namespace CSweet.UnitTests;

public sealed class InfrastructureProviderSecurityTests
{
    [Fact]
    public void RedactionRemovesStructuredAndEmbeddedCheckoutLinks()
    {
        var node = JsonNode.Parse("""
            {
              "consentUrl":"https://www.namecheap.com/apps/consent/secret-id",
              "note":"Open https://www.namecheap.com/apps/consent/secret-id to pay.",
              "contactDetails":{"registrant":{"email":"private@example.com"}},
              "nested":["https://www.namecheap.com/apps/consent/another-secret"]
            }
            """)!;

        ManifestInfrastructureProviderGateway.RedactNode(node);

        var result = node.ToJsonString();
        Assert.DoesNotContain("secret-id", result, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.com", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("CHECKOUT LINK WITHHELD", result, StringComparison.Ordinal);
    }
}
