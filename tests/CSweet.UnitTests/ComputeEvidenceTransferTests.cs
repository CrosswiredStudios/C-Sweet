using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeEvidenceTransferTests
{
    private static ComputeEvidenceArchive Request(byte[] bytes) => new(new('a',40), new('b',64), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, new('c',64));
    private static async Task BrowserTemplateAsync(ComputeBrokerTests.Fixture f)
    {
        var provision=await f.Db.ComputeOperations.SingleAsync(x=>x.Action==InfrastructureActions.Provision);
        var template=JsonSerializer.Deserialize<ComputeTemplate>(provision.TemplateJson,ComputeProtocol.Json)!;
        provision.TemplateJson=JsonSerializer.Serialize(template with{Features=new(template.Features){ComputeBrowserTest.Feature}},ComputeProtocol.Json);
        await f.Db.SaveChangesAsync();
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Signed_maximum_archive_is_verified_before_atomic_retention_and_duplicate_is_idempotent(bool corrupt)
    {
        await using var f=new ComputeResultTests.Fixture();await f.InitializeAsync();
        await f.Reconciler.ApplyAsync(f.Sign(),default);
        await BrowserTemplateAsync(f.Broker);
        await ComputeWorkloadTests.GrantAsync(f.Broker,InfrastructureActions.Execute);
        var bytes=new byte[ComputeEvidenceArchive.MaximumBytes];Array.Fill(bytes,(byte)251);var archive=Request(bytes);
        var env=await f.Broker.Db.ComputeEnvironments.SingleAsync();
        var accepted=await f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization,f.Broker.Installation,
            new(env.Id,env.Generation,"full-archive",new(EvidenceArchive:archive)),default);
        var row=await f.Broker.Db.ComputeOperations.SingleAsync(x=>x.Id==accepted.Id);row.Status="Dispatching";await f.Broker.Db.SaveChangesAsync();
        if(corrupt)bytes[0]^=1;
        var result=f.Result with{OperationId=accepted.Id,Generation=accepted.Generation,Action=InfrastructureActions.Execute,
            Workload=new(Artifact:new(archive.SourceCommitSha,archive.BuildSha256,archive.Sha256,archive.Length,archive.ScriptSha256,bytes))};
        var envelope=f.Sign(result);
        Assert.True(envelope.PayloadJson.Length>32768);
        var wire=ComputeResultTransport.SerializeEnvelope(envelope);
        Assert.True(wire.Length<=ComputeResultTransport.MaximumRequestBytes);
        Assert.Equal(envelope.PayloadJson,JsonSerializer.Deserialize<SignedComputeProviderResult>(wire,ComputeProtocol.Json)!.PayloadJson);
        if(corrupt)await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Reconciler.ApplyAsync(envelope,default));
        else {
            Assert.True(await f.Reconciler.ApplyAsync(envelope,default));Assert.False(await f.Reconciler.ApplyAsync(envelope,default));
            var observed=await f.Broker.Broker.ReadOperationAsync(f.Broker.Organization,f.Broker.Installation,accepted.Id,default);
            Assert.Null(observed.Result!.Artifact!.Content);Assert.Equal(archive.Sha256,observed.Result.Artifact.Sha256);
        }
    }
    [Theory]
    [InlineData(1)] [InlineData(956767)] [InlineData(4194304)]
    public async Task Whole_archive_is_verified_in_one_provider_operation_with_bounded_internal_io(int length)
    {
        var bytes = new byte[length]; RandomNumberGenerator.Fill(bytes); var request = Request(bytes); var offset = 0; var calls = 0;
        var result = await ComputeEvidenceArchiveTransfer.ExecuteAsync(request, (command, token) => {
            token.ThrowIfCancellationRequested(); calls++;
            Assert.InRange(command.MaximumOutputBytes,1,256*1024); Assert.Equal(5,command.TimeoutSeconds);
            Assert.Equal("/usr/bin/python3",command.Executable); Assert.Contains("O_NOFOLLOW", command.Arguments[1]);
            Assert.Contains(request.GuestPath,command.Arguments[1]);
            if(offset==0 && Environment.GetEnvironmentVariable("CSWEET_EVIDENCE_PROBE_EXPORT") is {Length:>0} export)
                File.WriteAllText(export,JsonSerializer.Serialize(new{script=command.Arguments[1],path=request.GuestPath,length=request.Length},ComputeProtocol.Json));
            var page = bytes.AsSpan(offset,command.MaximumOutputBytes).ToArray(); offset += page.Length;
            return Task.FromResult(new ComputeGuestWireResult(1,Guid.NewGuid(),command.RequestId,0,false,page,[],false));
        },default);
        request.Verify(result); Assert.Equal(bytes,result.Content); Assert.InRange(calls,1,16);
        Assert.Throws<ArgumentException>(()=>new ComputeWorkload(Command:new(Guid.NewGuid(),"/bin/true","/",[],30,8193)).Validate("linux"));
    }
    [Theory]
    [InlineData("truncated")] [InlineData("changed")] [InlineData("timeout")] [InlineData("cancel")]
    public async Task Incomplete_or_changed_archive_is_never_returned(string scenario)
    {
        var bytes = new byte[500000]; var request = Request(bytes);
        using var cancel = new CancellationTokenSource(); if(scenario=="cancel") cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(()=>ComputeEvidenceArchiveTransfer.ExecuteAsync(request,(command,token)=>{
            token.ThrowIfCancellationRequested(); var data=new byte[command.MaximumOutputBytes];
            if(scenario=="changed")data[0]=1;if(scenario=="truncated")data=data[..^1];
            return Task.FromResult(new ComputeGuestWireResult(1,Guid.NewGuid(),command.RequestId,scenario=="timeout"?null:0,scenario=="timeout",data,[],false));
        },cancel.Token));
    }
    [Fact]
    public void No_arbitrary_path_or_oversize_or_mixed_workload_is_admitted()
    {
        var request=Request([1]); Assert.Throws<ArgumentException>(()=>(request with{BuildSha256="../../secret"}).Validate());
        Assert.Throws<ArgumentException>(()=>(request with{ScriptSha256="/etc/passwd"}).Validate());
        Assert.Throws<ArgumentException>(()=>(request with{Length=4194305}).Validate());
        Assert.Throws<ArgumentException>(()=>new ComputeWorkload(EvidenceArchive:request).Validate("windows"));
        Assert.Throws<ArgumentException>(()=>new ComputeWorkload(EvidenceArchive:request,PublishPort:8080).Validate("linux"));
        Assert.Equal(new[]{InfrastructureActions.Execute},new ComputeWorkload(EvidenceArchive:request).Validate("linux").RequiredActions);
    }
    [Fact]
    public async Task Retained_archive_has_small_status_and_scoped_read_pages_without_a_new_compute_operation()
    {
        await using var f=new ComputeBrokerTests.Fixture();await f.SeedAsync();
        var env=await ComputeWorkloadTests.ReadyAsync(f);await BrowserTemplateAsync(f);await ComputeWorkloadTests.GrantAsync(f,InfrastructureActions.Execute);
        var bytes=new byte[956767];RandomNumberGenerator.Fill(bytes);var request=Request(bytes);
        var input=new CSweet.Application.Compute.RequestComputeWorkload(env.Id,env.Generation,"one-archive",new(EvidenceArchive:request));
        var accepted=await f.Broker.SubmitWorkloadAsync(f.Organization,f.Installation,input,default);
        Assert.Equal(accepted.Id,(await f.Broker.SubmitWorkloadAsync(f.Organization,f.Installation,input,default)).Id);
        var row=await f.Db.ComputeOperations.SingleAsync(x=>x.Id==accepted.Id);
        row.Status="Completed";row.ResultJson=JsonSerializer.Serialize(new ComputeWorkloadResult(Artifact:new(request.SourceCommitSha,request.BuildSha256,request.Sha256,request.Length,request.ScriptSha256,bytes)),ComputeProtocol.Json);
        await f.Db.SaveChangesAsync();
        var status=await f.Broker.ReadOperationAsync(f.Organization,f.Installation,accepted.Id,default);
        Assert.Null(status.Result!.Artifact!.Content);Assert.True(JsonSerializer.Serialize(status).Length<1000);
        using var collected=new MemoryStream();for(var offset=0;offset<bytes.Length;offset+=ComputeEvidenceArchive.PageBytes){
            var page=await f.Broker.ReadEvidencePageAsync(f.Organization,f.Installation,accepted.Id,offset,default);
            Assert.Equal(accepted.Id,page.ArtifactId);collected.Write(page.Content);
        }
        Assert.Equal(bytes,collected.ToArray());Assert.Equal(2,await f.Db.ComputeOperations.CountAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Broker.ReadEvidencePageAsync(f.Organization,Guid.NewGuid(),accepted.Id,0,default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>f.Broker.ReadEvidencePageAsync(f.Organization,f.Installation,accepted.Id,-1,default));
        (await f.Db.ScopedActionGrants.SingleAsync(x=>x.Action==InfrastructureActions.Read)).RevokedAt=DateTimeOffset.UtcNow.AddYears(-100);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Broker.ReadEvidencePageAsync(f.Organization,f.Installation,accepted.Id,0,default));
    }
}
