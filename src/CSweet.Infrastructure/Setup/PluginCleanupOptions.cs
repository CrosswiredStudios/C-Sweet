using System.Security.Cryptography;

namespace CSweet.Infrastructure.Setup;

/// <summary>Deployment-only trust. Neither packages nor account operators can supply these values.</summary>
public sealed class PluginCleanupOptions
{
    public const string SectionName="PluginCleanup";
    public string ClientCertificateThumbprint {get;set;}="";
    public List<PluginCleanupDestination> Destinations {get;set;}=[];
}
public sealed class PluginCleanupDestination
{
    public string Store {get;set;}="";
    public string Endpoint {get;set;}="";
    public Dictionary<string,string> ReceiptPublicKeys {get;set;}=new(StringComparer.Ordinal);
    internal bool IsValid()
    {
        if(Store is not ("external:backups" or "external:processors") ||
            !Uri.TryCreate(Endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="https" ||
            !string.IsNullOrEmpty(uri.UserInfo+uri.Query+uri.Fragment) || ReceiptPublicKeys.Count is <1 or >8) return false;
        try
        {
            foreach(var (id,pem) in ReceiptPublicKeys)
            {
                if(id.Length is <1 or >80 || !id.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                    pem.Length>16384 || !pem.StartsWith("-----BEGIN PUBLIC KEY-----",StringComparison.Ordinal)) return false;
                using var rsa=RSA.Create();rsa.ImportFromPem(pem);if(rsa.KeySize<3072) return false;
            }
            return true;
        }
        catch(Exception e) when(e is CryptographicException or ArgumentException) {return false;}
    }
}
