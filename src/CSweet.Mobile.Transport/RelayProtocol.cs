using System.Security.Cryptography;
using System.Text;

namespace CSweet.Mobile.Transport;

/// <summary>
/// Relay proof formats. Must match <c>MobileRelayProofs</c> in CSweet.ConnectionService.MobileRelay exactly.
/// ECDSA P-256 / SHA-256, IEEE P1363 signatures, base64url.
/// </summary>
public static class RelayProtocol
{
    public const string HubPath = "/hubs/mobile/v1";
    public const int MaxFrameBytes = 32 * 1024;

    public static string AttachMessage(string routeId, string nonce) => $"csweet-relay-attach\n{routeId}\n{nonce}";
    public static string OpenMessage(string routeId, string nonce) => $"csweet-relay-open\n{routeId}\n{nonce}";
    public static string TicketMessage(string routeId, string phoneKeyThumbprint, long expiresAt) =>
        $"csweet-relay-ticket\n{routeId}\n{phoneKeyThumbprint}\n{expiresAt}";

    /// <summary>Issued by Headquarters (with its route key) for one phone key; the relay admits that phone until expiry.</summary>
    public static string IssueTicket(ECDsa routeKey, string routeId, string phoneKeyThumbprint, DateTimeOffset expiresAt)
    {
        var body = Encoding.UTF8.GetBytes(TicketMessage(routeId, phoneKeyThumbprint, expiresAt.ToUnixTimeSeconds()));
        return Base64Url.Encode(body) + "." + Base64Url.Encode(routeKey.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>
    /// The directory's statement that <paramref name="subject"/> (a C-Sweet account) holds the phone key with
    /// <paramref name="phoneKeyThumbprint"/> and may ask <paramref name="installationId"/> to enroll it. Signed by the
    /// Market relay ticket key; Headquarters checks it inside the secure channel.
    /// </summary>
    public static string DeviceAssertionMessage(Guid installationId, string subject, string phoneKeyThumbprint, long expiresAt) =>
        $"csweet-device-assertion\n{installationId:D}\n{subject}\n{phoneKeyThumbprint}\n{expiresAt}";

    public sealed record DeviceAssertion(Guid InstallationId, string Subject, string PhoneKeyThumbprint, DateTimeOffset ExpiresAt);

    /// <summary>Verifies the signature with the directory key (base64url SPKI) and parses the assertion. Expiry is the caller's check.</summary>
    public static DeviceAssertion? ReadDeviceAssertion(string? assertion, string directoryKey)
    {
        if (string.IsNullOrEmpty(assertion) || assertion.Length > 2048) return null;
        var dot = assertion.IndexOf('.');
        if (dot <= 0 || dot == assertion.Length - 1) return null;
        try
        {
            var body = Decode(assertion[..dot]);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Decode(directoryKey), out _);
            if (!key.VerifyData(body, Decode(assertion[(dot + 1)..]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            var parts = Encoding.UTF8.GetString(body).Split('\n');
            if (parts.Length != 5 || parts[0] != "csweet-device-assertion" || !Guid.TryParse(parts[1], out var installation) ||
                string.IsNullOrEmpty(parts[2]) || string.IsNullOrEmpty(parts[3]) || !long.TryParse(parts[4], out var expires)) return null;
            return new DeviceAssertion(installation, parts[2], parts[3], DateTimeOffset.FromUnixTimeSeconds(expires));
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return null;
        }
    }

    internal static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>Short fingerprint people compare between the phone and the approving session: first 16 characters of the pin in groups of four.</summary>
    public static string Fingerprint(string pin) =>
        string.Join(' ', Enumerable.Range(0, 4).Select(i => pin.Substring(i * 4, 4)));

    public static string Sign(ECDsa key, string message) =>
        Base64Url.Encode(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static string PublicKey(ECDsa key) => Base64Url.Encode(key.ExportSubjectPublicKeyInfo());

    /// <summary>base64url SHA-256 of the SubjectPublicKeyInfo. Also the TLS pin of a certificate made from this key.</summary>
    public static string Thumbprint(ECDsa key) => Base64Url.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
}

internal static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
