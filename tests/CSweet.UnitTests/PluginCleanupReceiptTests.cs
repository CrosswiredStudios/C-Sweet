using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using F=CSweet.UnitTests.PluginAccountFlowTests.Fixture;

namespace CSweet.UnitTests;

public sealed class PluginCleanupReceiptTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData("hash")][InlineData("signature")][InlineData("expired")][InlineData("future")]
    [InlineData("pending")][InlineData("wrong-key")][InlineData("unknown-version")]
    public async Task ForgedStaleOrWrongScopeReceiptsNeverConfirmCleanup(string error)
    {
        await using var f=await F.Create();using var rsa=RSA.Create(3072);var copy=await Seed(f);
        var transport=new SigningTransport(rsa){Error=error};var participant=Create(f,rsa,transport);
        Assert.False(await participant.DeleteAsync(copy.AccountGroupId,null,copy.ConsumerInstallationId,copy.RecordId,default));
        Assert.Empty(await f.Inner.Db.PluginAccountCleanupReceipts.ToArrayAsync());Assert.Null(copy.DeletedAt);
    }
    [Fact]
    public async Task ExactSignedReceiptPersistsAndReplaysOnlyForItsOriginalCopyRevision()
    {
        await using var f=await F.Create();using var rsa=RSA.Create(3072);var copy=await Seed(f);
        var transport=new SigningTransport(rsa);var participant=Create(f,rsa,transport);
        Assert.True(await participant.DeleteAsync(copy.AccountGroupId,null,copy.ConsumerInstallationId,copy.RecordId,default));
        Assert.True(await participant.DeleteAsync(copy.AccountGroupId,null,copy.ConsumerInstallationId,copy.RecordId,default));Assert.Equal(1,transport.Requests);
        var receipt=await f.Inner.Db.PluginAccountCleanupReceipts.SingleAsync();Assert.Equal(copy.Id,receipt.CopyId);Assert.Equal(copy.Revision,receipt.CopyRevision);
        copy.Revision++;await f.Inner.Db.SaveChangesAsync();
        Assert.False(await participant.DeleteAsync(copy.AccountGroupId,null,copy.ConsumerInstallationId,copy.RecordId,default));
        receipt.RequestHash="tampered";await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inner.Db.SaveChangesAsync());
    }
    [Fact]
    public async Task CallerCannotChooseAnotherOrganizationOrRecordAndUnconfiguredAdaptersRemainUnavailable()
    {
        await using var f=await F.Create();using var rsa=RSA.Create(3072);var copy=await Seed(f);var transport=new SigningTransport(rsa);
        var participant=Create(f,rsa,transport);
        Assert.False(await participant.DeleteAsync(copy.AccountGroupId,Guid.NewGuid(),copy.ConsumerInstallationId,copy.RecordId,default));
        Assert.False(await participant.DeleteAsync(copy.AccountGroupId,null,copy.ConsumerInstallationId,Guid.NewGuid(),default));Assert.Equal(0,transport.Requests);
        transport.IsConfigured=false;Assert.False(participant.IsConfigured);
        Assert.False(new PluginCleanupDestination{Store="external:backups",Endpoint="http://untrusted.test",ReceiptPublicKeys=new(){["key"]=rsa.ExportSubjectPublicKeyInfoPem()}}.IsValid());
        Assert.False(new PluginCleanupDestination{Store="external:retained-evidence",Endpoint="https://trusted.test/erase",ReceiptPublicKeys=new(){["key"]=rsa.ExportSubjectPublicKeyInfoPem()}}.IsValid());
    }
    private static PluginSignedCleanupParticipant Create(F f,RSA rsa,IPluginCleanupTransport transport)=>new("external:backups",f.Inner.Db,
        Options.Create(new PluginCleanupOptions{Destinations=[new(){Store="external:backups",Endpoint="https://cleanup.example.com/erase",ReceiptPublicKeys=new(){["key"]=rsa.ExportSubjectPublicKeyInfoPem()}}]}),transport,TimeProvider.System);
    private static async Task<PluginAccountDataCopy> Seed(F f)
    {
        var group=new PluginAccountGroup{Id=Guid.NewGuid(),PluginInstallationId=f.Id,State=PluginAccountState.Disconnecting};
        var copy=new PluginAccountDataCopy{Id=Guid.NewGuid(),AccountGroupId=group.Id,ConsumerInstallationId=f.Id,RecordId=group.Id,Store="external:backups",CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-2)};
        f.Inner.Db.PluginAccountGroups.Add(group);f.Inner.Db.PluginAccountDataCopies.Add(copy);
        f.Inner.Db.PluginAccountDeletions.Add(new(){AccountGroupId=group.Id,PluginInstallationId=f.Id,RequestedAt=DateTimeOffset.UtcNow.AddMinutes(-1)});
        await f.Inner.Db.SaveChangesAsync();return copy;
    }
    private sealed class SigningTransport(RSA rsa):IPluginCleanupTransport
    {
        public bool IsConfigured {get;set;}=true;public string? Error {get;set;}public int Requests {get;private set;}
        public Task<PluginSignedCleanupConfirmation?> RequestAsync(Uri endpoint,PluginCleanupRequest request,CancellationToken ct)
        {
            Requests++;var now=DateTimeOffset.UtcNow;
            var receipt=new PluginCleanupConfirmation(Error=="unknown-version"?"2":"1",Error=="hash"?new string('a',64):request.RequestHash,
                Error=="pending"?"Pending":"Deleted",Error=="future"?now.AddHours(1):now,Error=="expired"?now.AddMinutes(-1):now.AddHours(2));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(receipt,Json);var signature=rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            if(Error=="signature")signature[0]^=1;
            return Task.FromResult<PluginSignedCleanupConfirmation?>(new(Error=="wrong-key"?"unreviewed":"key",Convert.ToBase64String(bytes),Convert.ToBase64String(signature)));
        }
    }
}
