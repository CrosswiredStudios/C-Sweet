using System.Text;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class SecretResponseSanitizerTests
{
    [Theory]
    [InlineData("/items/*/stream/key")]
    [InlineData("/items/0/stream/key")]
    public async Task SecretNeverReachesRuntime(string selector)
    {
        var captured = new List<string>();
        var sanitized = await SecretResponseSanitizer.SanitizeAsync(Encoding.UTF8.GetBytes("""{"items":[{"stream":{"key":"sensitive-encoder-key"},"title":"Live"}]}"""),
            [selector], (_, value, _) => { captured.Add(value); return Task.FromResult("opaque-ref"); }, default);
        var output = Encoding.UTF8.GetString(sanitized);
        Assert.DoesNotContain("sensitive-encoder-key", output);
        Assert.Contains("secretReference", output); Assert.Contains("Live", output);
        Assert.Equal("sensitive-encoder-key", Assert.Single(captured));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"key\":\"first\",\"key\":\"hidden\"}")]
    [InlineData("{\"items\":{\"stream\":{\"key\":\"secret\"}}}")]
    public async Task MalformedSecretBearingResponseIsWithheldBeforeVaultWrites(string body)
    {
        var writes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SecretResponseSanitizer.SanitizeAsync(Encoding.UTF8.GetBytes(body),
            ["/items/*/stream/key"], (_, _, _) => { writes++; return Task.FromResult("opaque"); }, default));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task VaultFailureCannotReturnPartlyScrubbedContent() => await Assert.ThrowsAsync<IOException>(() =>
        SecretResponseSanitizer.SanitizeAsync(Encoding.UTF8.GetBytes("""{"key":"secret"}"""), ["/key"],
            (_, _, _) => throw new IOException("Vault unavailable"), default));

    [Fact]
    public async Task EmptyProviderCollectionsNeedNoSecrets()
    {
        var sanitized = await SecretResponseSanitizer.SanitizeAsync(Encoding.UTF8.GetBytes("""{"items":[]}"""),
            ["/items/*/stream/key"], (_, _, _) => throw new InvalidOperationException(), default);
        Assert.Equal("{\"items\":[]}", Encoding.UTF8.GetString(sanitized));
    }
}
