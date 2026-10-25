using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

// Trusted host service seam: tests may inject it; packages and HTTP callers cannot.
public interface IPluginCleanupTransport
{
    bool IsConfigured {get;}
    Task<PluginSignedCleanupConfirmation?> RequestAsync(Uri endpoint,PluginCleanupRequest request,CancellationToken ct);
}
public sealed record PluginCleanupRequest(string Version,Guid CopyId,long CopyRevision,string Store,Guid AccountId,
    Guid? OrganizationId,Guid ConsumerInstallationId,Guid RecordId,DateTimeOffset DeleteThrough,string RequestHash);
public sealed record PluginCleanupConfirmation(string Version,string RequestHash,string Status,DateTimeOffset CompletedAt,DateTimeOffset ExpiresAt);
public sealed record PluginSignedCleanupConfirmation(string KeyId,string Payload,string Signature);

/// <summary>Mutually authenticated TLS, fixed deployment destination, no redirects/cookies or provider content.</summary>
public sealed class PluginCleanupTransport(IOptions<PluginCleanupOptions> options):IPluginCleanupTransport
{
    private X509Certificate2? Certificate()
    {
        var thumbprint=options.Value.ClientCertificateThumbprint.Replace(" ","",StringComparison.Ordinal);
        if(thumbprint.Length is not (40 or 64) || !thumbprint.All(Uri.IsHexDigit)) return null;
        using var store=new X509Store(StoreName.My,StoreLocation.CurrentUser);store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint,thumbprint,false).Cast<X509Certificate2>()
            .SingleOrDefault(c=>c.HasPrivateKey && c.NotBefore.ToUniversalTime()<=DateTime.UtcNow && c.NotAfter.ToUniversalTime()>DateTime.UtcNow);
    }
    public bool IsConfigured
    {get{try{using var certificate=Certificate();return certificate!=null;}catch(Exception e) when(e is System.Security.Cryptography.CryptographicException or InvalidOperationException){return false;}}}
    public async Task<PluginSignedCleanupConfirmation?> RequestAsync(Uri endpoint,PluginCleanupRequest request,CancellationToken ct)
    {
        using var certificate=Certificate();if(certificate==null) return null;
        using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,ClientCertificateOptions=ClientCertificateOption.Manual};
        handler.ClientCertificates.Add(certificate);
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(20)};
        using var message=new HttpRequestMessage(HttpMethod.Post,endpoint){Content=JsonContent.Create(request)};
        using var response=await client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.StatusCode!=System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength>65536) return null;
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        var bytes=new byte[65537];var count=0;
        while(count<bytes.Length){var read=await stream.ReadAsync(bytes.AsMemory(count),ct);if(read==0)break;count+=read;}
        if(count>65536)return null;
        return JsonSerializer.Deserialize<PluginSignedCleanupConfirmation>(bytes.AsSpan(0,count),new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow});
    }
}
