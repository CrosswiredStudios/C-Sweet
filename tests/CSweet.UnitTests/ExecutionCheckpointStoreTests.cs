using System.IO.Compression;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ExecutionCheckpointStoreTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"csweet-checkpoint-tests-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckpointHeadIsRevisionedIdempotentAndBlobsAreProtected()
    {
        await using var f=await CreateAsync();
        var bytes=Archive("events/one.json","private conversation content");
        var input=new ExecutionCheckpointWrite("work","engine-1",0,1,bytes,"write-1",SourceArchive:Archive("app.txt","source"));
        var saved=await f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,input,default);
        var replay=await f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,input,default);
        Assert.Equal(1,saved.Revision); Assert.Equal(1,replay.Revision);
        Assert.Equal(bytes,(await f.Store.ReadAsync(f.Organization,f.Installation,f.Runtime,new("work","engine-1"),default))!.EngineArchive);
        Assert.All(Directory.GetFiles(_root,"*.blob",SearchOption.AllDirectories),file=>
            Assert.False(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes)));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,
            input with { IdempotencyKey="write-2",EventCursor=2 },default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.ReadAsync(f.Organization,f.Installation,f.Runtime,new("work","engine-2"),default));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData(".git/config")]
    public async Task UnsafeArchivesNeverAdvanceHead(string path)
    {
        await using var f=await CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,
            new("work","engine-1",0,1,Archive(path,"payload"),"write"),default));
        Assert.Empty(f.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task FencedExpiredAndForeignWorkCannotReadOrAdvanceCheckpoints()
    {
        await using var f=await CreateAsync();
        var input=new ExecutionCheckpointWrite("work","engine-1",0,1,Archive("events/one.json","event"),"write");
        await f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,input,default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Store.ReadAsync(f.Organization,Guid.NewGuid(),f.Runtime,new("work","engine-1"),default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,8,input,default));
        f.Attempt.LeaseExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Store.ReadAsync(f.Organization,f.Installation,f.Runtime,new("work","engine-1"),default));
    }

    [Fact]
    public async Task ChunksAreProtectedAndOnlyCompleteDigestCanCommit()
    {
        await using var f=await CreateAsync();
        var bytes=Archive("events/one.json",new string('x',5*1024*1024),CompressionLevel.NoCompression);
        var digest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        for(var offset=0;offset<bytes.Length;offset+=ExecutionCheckpointTransfer.ChunkBytes)
        {
            await f.Store.StageAsync(f.Organization,f.Installation,f.Runtime,7,new("stage","work","engine-1",digest,offset,bytes.Length,
                bytes.AsSpan(offset,Math.Min(ExecutionCheckpointTransfer.ChunkBytes,bytes.Length-offset)).ToArray(),null,null),default);
            if(offset==0) await Assert.ThrowsAsync<InvalidDataException>(()=>f.Store.AssembleAsync(f.Organization,f.Installation,"work",digest,default));
        }
        Assert.Equal(bytes,await f.Store.AssembleAsync(f.Organization,f.Installation,"work",digest,default));
        Assert.Empty(f.Db.PluginOperationalStates); // Staging is never a committed resume point.
    }

    [Fact]
    public async Task CleanupRetainsCurrentHeadAndReclaimsOldUnreferencedBlobs()
    {
        await using var f=await CreateAsync();
        var one=new ExecutionCheckpointWrite("work","engine-1",0,1,Archive("one.json","one"),"one");
        var first=await f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,one,default);
        await f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,one with {ExpectedRevision=1,EventCursor=2,EngineArchive=Archive("two.json","two"),IdempotencyKey="two"},default);
        foreach(var file in Directory.GetFiles(_root,"*.blob",SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-3));
        await f.Store.CollectOrphansAsync(DateTimeOffset.UtcNow,default);
        Assert.Single(Directory.GetFiles(_root,"*.blob",SearchOption.AllDirectories));
        Assert.Equal(2,(await f.Store.ReadAsync(f.Organization,f.Installation,f.Runtime,new("work","engine-1"),default))!.Revision);
    }

    [Fact]
    public async Task StorageLimitStopsUploadsWithoutAdvancingAHead()
    {
        await using var f=await CreateAsync();
        var directory=f.Store.DirectoryPath(f.Organization,f.Installation);
        Directory.CreateDirectory(directory);
        using(var file=File.Create(Path.Combine(directory,"quota-test.blob"))) file.SetLength(2L*1024*1024*1024);
        var error=await Assert.ThrowsAsync<IOException>(()=>f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,
            new("work","engine-1",0,1,Archive("event.json","event"),"write"),default));
        Assert.Contains("storage_limit",error.Message);
        Assert.Empty(f.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task RemovedInstallationReclaimsItsPrivateBlobsAndMetadataAfterTheGracePeriod()
    {
        await using var f=await CreateAsync();
        await f.Store.WriteAsync(f.Organization,f.Installation,f.Runtime,7,new("work","engine-1",0,1,Archive("event.json","private"),"write"),default);
        var directory=f.Store.DirectoryPath(f.Organization,f.Installation);
        f.Db.AgentWorkAttempts.RemoveRange(f.Db.AgentWorkAttempts);
        await f.Db.SaveChangesAsync();
        f.Db.AgentRuntimeInstances.RemoveRange(f.Db.AgentRuntimeInstances);
        f.Db.AgentWorkItems.RemoveRange(f.Db.AgentWorkItems);
        await f.Db.SaveChangesAsync();
        f.Db.AgentInstallations.Remove(await f.Db.AgentInstallations.SingleAsync());
        await f.Db.SaveChangesAsync();
        await f.Store.CollectOrphansAsync(DateTimeOffset.UtcNow.AddDays(2),default);
        Assert.False(Directory.Exists(directory)); Assert.Empty(f.Db.PluginOperationalStates);
    }

    private async Task<Fixture> CreateAsync()
    {
        var db=new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x=>x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var organization=Guid.NewGuid(); var installation=Guid.NewGuid(); var runtime=Guid.NewGuid(); var work=Guid.NewGuid();
        var agent=new AgentInstallation {Id=installation,BusinessId=organization.ToString(),IsEnabled=true,
            Grant=new AgentInstallationGrant {Id=Guid.NewGuid(),AgentInstallationId=installation,GrantRevision=7}};
        var instance=new AgentRuntimeInstance {Id=runtime,AgentInstallation=agent,AgentInstallationId=installation};
        instance.TransitionTo(AgentRuntimeStatus.Starting,DateTimeOffset.UtcNow);
        instance.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession,DateTimeOffset.UtcNow);
        instance.TransitionTo(AgentRuntimeStatus.Running,DateTimeOffset.UtcNow);
        var attempt=new AgentWorkAttempt {Id=Guid.NewGuid(),RuntimeInstance=instance,RuntimeInstanceId=runtime,AgentWorkItemId=work,
            LeaseExpiresAt=DateTimeOffset.UtcNow.AddMinutes(20),AgentWorkItem=new AgentWorkItem {Id=work,OrganizationId=organization.ToString(),AgentInstallationId=installation,Status=AgentWorkStatus.Leased}};
        db.CoreOrganizations.Add(new Organization {Id=organization,Name="Checkpoint test",Status=OrganizationStatus.Active});
        db.AgentInstallations.Add(agent); db.AgentRuntimeInstances.Add(instance); db.AgentWorkAttempts.Add(attempt);
        await db.SaveChangesAsync();
        var store=new ExecutionCheckpointStore(db,Options.Create(new AgentRuntimeManagerOptions {WorkspaceSnapshotStorePath=_root}),new EphemeralDataProtectionProvider());
        return new(db,store,organization,installation,runtime,attempt);
    }

    private static byte[] Archive(string path,string content,CompressionLevel compression=CompressionLevel.Optimal)
    {
        using var buffer=new MemoryStream();
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
        using(var writer=new StreamWriter(zip.CreateEntry(path,compression).Open())) writer.Write(content);
        return buffer.ToArray();
    }
    private sealed record Fixture(CSweetDbContext Db,ExecutionCheckpointStore Store,Guid Organization,Guid Installation,Guid Runtime,AgentWorkAttempt Attempt) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()=>Db.DisposeAsync();
    }
    public void Dispose() { if(Directory.Exists(_root)) Directory.Delete(_root,true); }
}
