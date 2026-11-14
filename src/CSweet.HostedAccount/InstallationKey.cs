using System.Security.Cryptography;
using System.Text;

namespace CSweet.HostedAccount;

/// <summary>
/// The installation's ECDSA P-256 key. The public key (SubjectPublicKeyInfo, base64url) is shown to the person at
/// approval and registered with the account issuer; proofs are ECDSA-SHA256 signatures in IEEE P1363 form.
/// The message formats must match the Market's <c>InstallationKeys</c> exactly.
/// </summary>
public sealed class InstallationKey : IDisposable
{
    private readonly ECDsa _key;

    private InstallationKey(ECDsa key) => _key = key;

    public static InstallationKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static InstallationKey Import(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new InstallationKey(key);
    }

    /// <summary>Private key for protected storage. Never log or display it.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    public string PublicKey => Base64Url.Encode(_key.ExportSubjectPublicKeyInfo());

    public string Thumbprint => Base64Url.Encode(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()));

    public string SignRegistration(long timestamp, string accessToken) =>
        Sign($"csweet-installation-register\n{Thumbprint}\n{timestamp}\n{Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)))}");

    public string SignRetirement(Guid installationId, long timestamp) =>
        Sign($"csweet-installation-retire\n{installationId:D}\n{timestamp}");

    private string Sign(string message) =>
        Base64Url.Encode(_key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => _key.Dispose();
}

internal static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
