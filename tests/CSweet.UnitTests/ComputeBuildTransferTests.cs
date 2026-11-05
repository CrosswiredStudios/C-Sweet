using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeBuildTransferTests
{
    internal static ComputeBuildArchive Input(byte[] bytes) => new(new('a',40), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
        Enumerable.Range(0,(bytes.Length+65535)/65536).Select(i => Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(i*65536,Math.Min(65536,bytes.Length-i*65536))))).ToArray());

    [Theory]
    [InlineData("valid")] [InlineData("wrong-tenant")] [InlineData("wrong-installation")]
    [InlineData("changed-part")] [InlineData("wrong-dispatch")]
    public async Task Staged_input_and_provider_reads_are_bound_to_the_owner_and_live_signed_dispatch(string scenario)
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync(); await f.Reconciler.ApplyAsync(f.Sign(), default);
        await ComputeWorkloadTests.GrantAsync(f.Broker, InfrastructureActions.Execute);
        var db = f.Broker.Db;
        var provision = await db.ComputeOperations.SingleAsync(x => x.Action == InfrastructureActions.Provision);
        var template = JsonSerializer.Deserialize<ComputeTemplate>(provision.TemplateJson, ComputeProtocol.Json)!;
        provision.TemplateJson = JsonSerializer.Serialize(template with { Features = new(template.Features) { ComputeBrowserTest.Feature } }, ComputeProtocol.Json);
        var bytes = new byte[130001]; RandomNumberGenerator.Fill(bytes); var input = Input(bytes);
        for (var offset=0;offset<bytes.Length;offset+=65536)
        {
            var part = new ComputeBuildInputPart(input.SourceCommitSha,input.BuildSha256,input.Length,offset,
                bytes.AsSpan(offset,Math.Min(65536,bytes.Length-offset)).ToArray());
            if (scenario=="changed-part" && offset==0) part.Content[0]^=1;
            db.PluginOperationalStates.Add(new() { Id=Guid.NewGuid(), OrganizationId=scenario=="wrong-tenant"?Guid.NewGuid():f.Broker.Organization,
                AgentInstallationId=scenario=="wrong-installation"?Guid.NewGuid():f.Broker.Installation,
                Kind="agent-operating-state",ExternalKey=input.PartKey(offset),PayloadJson=JsonSerializer.Serialize(new {schemaId=ComputeBuildArchive.SchemaId,payload=part},ComputeProtocol.Json) });
        }
        await db.SaveChangesAsync(); var environment=await db.ComputeEnvironments.SingleAsync(); var generation=environment.Generation;
        if(scenario is "wrong-tenant" or "wrong-installation" or "changed-part")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization,f.Broker.Installation,
                new(environment.Id,generation,"bulk",new(BuildArchive:input)),default));
            Assert.Equal(generation,(await db.ComputeEnvironments.SingleAsync()).Generation);return;
        }
        var op=await f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization,f.Broker.Installation,new(environment.Id,generation,"bulk",new(BuildArchive:input)),default);
        var row=await db.ComputeOperations.SingleAsync(x=>x.Id==op.Id);row.Status="Dispatching";row.DispatchLeaseId=Guid.NewGuid();row.DispatchLeaseExpiresAt=new ComputeBrokerTests.Clock().GetUtcNow().AddMinutes(3);await db.SaveChangesAsync();
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust=new ComputeResultTests.Trust(new("node",f.Broker.Organization,environment.ProviderNodeId!.Value,environment.ProviderId!,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var clock=new ComputeBrokerTests.Clock();var access=new ComputeBuildInputAccess(db,new(trust,clock),f.Broker.Broker,clock);
        var proof=new ComputeProviderWorkRequest(f.Broker.Organization,environment.ProviderNodeId.Value,environment.ProviderId!,Guid.NewGuid(),clock.GetUtcNow(),clock.GetUtcNow().AddMinutes(1),
            OperationId:op.Id,InputOffset:65536,DispatchId:scenario=="wrong-dispatch"?Guid.NewGuid():row.DispatchLeaseId);
        var data=JsonSerializer.Serialize(proof,ComputeProtocol.Json);var signed=new SignedComputeProviderWorkRequest("node",data,Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(data),HashAlgorithmName.SHA256)));
        if(scenario=="wrong-dispatch") { await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>access.ReadAsync(signed,default));return; }
        Assert.Equal(bytes.AsSpan(65536,Math.Min(65536,bytes.Length-65536)).ToArray(),(await access.ReadAsync(signed,default)).Content);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new ComputeProviderWorkRequestVerifier(trust,clock).VerifyAsync(signed,default));
        foreach(var grant in await db.ScopedActionGrants.ToListAsync()) grant.RevokedAt=clock.GetUtcNow();
        await db.SaveChangesAsync();await Assert.ThrowsAnyAsync<Exception>(()=>access.ReadAsync(signed,default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Whole_input_is_verified_before_guest_writes_and_no_application_command_is_executed(bool corrupt)
    {
        var bytes=new byte[530000];RandomNumberGenerator.Fill(bytes);var input=Input(bytes);var calls=0;var commands=new List<string>();
        var task=ComputeBuildArchiveTransfer.ExecuteAsync(input,(offset,_)=>{
            var part=bytes.AsSpan(offset,Math.Min(65536,bytes.Length-offset)).ToArray();if(corrupt)part[0]^=1;return Task.FromResult(part);
        },(command,_)=>{
            calls++;Assert.Equal("/usr/bin/python3",command.Executable);Assert.Contains("O_NOFOLLOW",command.Arguments[1]);
            command.ValidateAndSnapshot("linux");commands.Add(command.Arguments[1]);
            return Task.FromResult(new ComputeGuestWireResult(1,Guid.NewGuid(),command.RequestId,0,false,calls==1?Encoding.UTF8.GetBytes("missing"):[],[],false));
        },default);
        if(corrupt){await Assert.ThrowsAsync<InvalidDataException>(()=>task);Assert.Equal(0,calls);return;}
        var receipt=await task;Assert.Equal(input.BuildSha256,receipt.BuildSha256);Assert.InRange(calls,3,40);
        if(Environment.GetEnvironmentVariable("CSWEET_BUILD_TRANSFER_PROBE_EXPORT") is { Length:>0 } path)
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{commands,sha256=input.BuildSha256,length=input.Length},ComputeProtocol.Json));
        Assert.Throws<ArgumentException>(()=>new ComputeWorkload(BuildArchive:input,Command:new(Guid.NewGuid(),"/bin/true","/",[],1,1)).Validate("linux"));
    }
}
