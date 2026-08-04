using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSweet.TrustedServices;

public sealed record GitHubAppConfigurationPayload(
    long AppId,
    string PrivateKeyBase64,
    long Revision);

public sealed record SealedGitHubAppConfiguration(
    string Nonce,
    string Ciphertext,
    string Tag);

public sealed record GitHubAppConfigurationStatus(
    bool Configured,
    long? AppId,
    long Revision,
    string? AppSlug,
    string? AppName,
    string? FailureMessage = null);

public static class GitHubAppConfigurationEnvelope
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes(
        "CSweet.GitHubAppConfigurationEnvelope.v1");

    public static SealedGitHubAppConfiguration Seal(
        GitHubAppConfigurationPayload payload,
        TrustedServiceAuthenticationOptions options,
        string hostKind)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(DeriveKey(options), tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(hostKind));
        CryptographicOperations.ZeroMemory(plaintext);
        return new SealedGitHubAppConfiguration(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    public static GitHubAppConfigurationPayload Open(
        SealedGitHubAppConfiguration envelope,
        TrustedServiceAuthenticationOptions options,
        string hostKind)
    {
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DeriveKey(options), tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(hostKind));
        try
        {
            return JsonSerializer.Deserialize<GitHubAppConfigurationPayload>(plaintext)
                ?? throw new InvalidOperationException("The sealed GitHub App configuration was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(TrustedServiceAuthenticationOptions options)
    {
        if (!TrustedServiceAuthenticationExtensions.TryGetKey(options, out var root))
            throw new InvalidOperationException("Trusted service authentication is not configured.");
        return HMACSHA256.HashData(root, Purpose);
    }
}
