using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Confirms deployment-owned external copies; never an agent/publisher assertion.</summary>
public sealed class PluginSignedCleanupParticipant(string store,CSweetDbContext db,IOptions<PluginCleanupOptions> options,
    IPluginCleanupTransport transport,TimeProvider clock):IPluginAccountDataDeletionParticipant
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public string Store=>store;
    private PluginCleanupDestination? Destination=>options.Value.Destinations.Count(x=>x.Store==Store)==1
        ? options.Value.Destinations.Single(x=>x.Store==Store) : null;
    public bool IsConfigured=>Destination?.IsValid()==true && transport.IsConfigured;
    public async Task<bool> DeleteAsync(Guid accountId,Guid? organizationId,Guid consumerInstallationId,Guid recordId,CancellationToken ct)
    {
        if(!IsConfigured)return false;
        var copy=await db.PluginAccountDataCopies.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountGroupId==accountId && x.OrganizationId==organizationId &&
            x.ConsumerInstallationId==consumerInstallationId && x.RecordId==recordId && x.Store==Store,ct);
        if(copy==null || !await db.PluginAccountGroups.AnyAsync(x=>x.Id==accountId && x.State==PluginAccountState.Disconnecting,ct))return false;
        if(copy.DeletedAt!=null)return true;
        var deletion=await db.PluginAccountDeletions.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountGroupId==accountId && x.CompletedAt==null,ct);
        // Obligation rows (for example backups) are created after the account fence;
        // they are references, not late provider-data writes. Bind the cutoff to that fence.
        if(deletion==null)return false;
        var request=new PluginCleanupRequest("1",copy.Id,copy.Revision,Store,accountId,organizationId,consumerInstallationId,recordId,deletion.RequestedAt,"");
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,Json)));
        request=request with {RequestHash=hash};
        var saved=await db.PluginAccountCleanupReceipts.AsNoTracking().SingleOrDefaultAsync(x=>x.CopyId==copy.Id,ct);
        if(saved!=null)return saved.CopyRevision==copy.Revision && saved.RequestHash==hash;
        if(copy.CleanupNextAttemptAt>clock.GetUtcNow())return false;
        var dispatch=db.PluginAccountDataCopies.Local.SingleOrDefault(x=>x.Id==copy.Id) ?? await db.PluginAccountDataCopies.SingleAsync(x=>x.Id==copy.Id,ct);
        dispatch.CleanupNextAttemptAt=clock.GetUtcNow().AddMinutes(5);
        try{await db.SaveChangesAsync(ct);}catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();return false;}
        try
        {
            var destination=Destination!;
            var envelope=await transport.RequestAsync(new(destination.Endpoint),request,ct);
            if(envelope==null || string.IsNullOrEmpty(envelope.KeyId) || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature) || envelope.Payload.Length>16384 || envelope.Signature.Length>2048 ||
                !destination.ReceiptPublicKeys.TryGetValue(envelope.KeyId,out var pem))return false;
            var payload=Convert.FromBase64String(envelope.Payload);var signature=Convert.FromBase64String(envelope.Signature);
            using var rsa=RSA.Create();rsa.ImportFromPem(pem);
            if(!rsa.VerifyData(payload,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))return false;
            using var document=JsonDocument.Parse(payload);
            if(document.RootElement.ValueKind!=JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=5 ||
                document.RootElement.EnumerateObject().Count()!=5)return false;
            var receipt=JsonSerializer.Deserialize<PluginCleanupConfirmation>(payload,Json);var now=clock.GetUtcNow();
            if(receipt is not {Version:"1",Status:"Deleted"} || receipt.RequestHash!=hash || receipt.CompletedAt<deletion.RequestedAt ||
                receipt.CompletedAt>now.AddMinutes(1) || receipt.ExpiresAt<=now || receipt.ExpiresAt>receipt.CompletedAt.AddDays(1))return false;
            db.PluginAccountCleanupReceipts.Add(new(){CopyId=copy.Id,CopyRevision=copy.Revision,RequestHash=hash,KeyId=envelope.KeyId,
                Payload=envelope.Payload,Signature=envelope.Signature,ConfirmedAt=now});
            await db.SaveChangesAsync(ct);return true;
        }
        catch(Exception e) when(e is HttpRequestException or JsonException or CryptographicException or FormatException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {return false;}
    }
}
