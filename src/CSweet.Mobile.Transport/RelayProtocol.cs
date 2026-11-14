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
