using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeNodeDependencyTests
{
    [Fact]
    public void Existing_workload_bytes_and_signature_digests_remain_compatible_without_dependency_restore()
    {
        var command=new ComputeGuestCommand(Guid.NewGuid(),"/bin/echo","/tmp",["retained request"],30,8192);
        var original="{\"command\":"+JsonSerializer.Serialize(command,ComputeProtocol.Json)+",\"publishPort\":null}";
        var restored=JsonSerializer.Deserialize<ComputeWorkload>(original,ComputeProtocol.Json)!;
        Assert.Equal(original,JsonSerializer.Serialize(restored,ComputeProtocol.Json));
        Assert.Equal(ComputeProtocol.Digest(original),ComputeProtocol.Digest(JsonSerializer.Serialize(new ComputeWorkload(command),ComputeProtocol.Json)));
    }
    internal static ComputeNodeDependencies Dependencies(byte[]? bytes = null) => new(new string('a',64),
        [new("https://registry.npmjs.org/phaser/-/phaser-3.90.0.tgz", "sha512-"+Convert.ToBase64String(SHA512.HashData(bytes ?? [1,2,3])))]);

    [Theory]
    [InlineData("http://registry.npmjs.org/a.tgz")] [InlineData("https://127.0.0.1/a.tgz")]
    [InlineData("https://registry.npmjs.org.evil.test/a.tgz")] [InlineData("https://registry.npmjs.org:444/a.tgz")]
    [InlineData("https://user:secret@registry.npmjs.org/a.tgz")] [InlineData("file:///tmp/a.tgz")]
    public void Download_locations_cannot_select_host_private_or_other_registry_endpoints(string url) =>
        Assert.Throws<ArgumentException>(()=>(Dependencies() with { Packages=[Dependencies().Packages[0] with { Url=url }] }).Validate());

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.1.2.3")] [InlineData("169.254.169.254")]
    [InlineData("::1")] [InlineData("::ffff:192.168.1.1")] [InlineData("168.63.129.16")]
    public void Public_registry_dns_cannot_redirect_to_private_or_metadata_addresses(string address) =>
        Assert.False(new ComputePublicInternetPolicy().IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("absent")] [InlineData("registry-denied")] [InlineData("granted")] [InlineData("revoked-before-dispatch")]
    public async Task Restore_requires_both_grants_and_dispatch_revalidates_the_same_authority(string scenario)
    {
        await using var f=new ComputeDispatchTests.Fixture();await f.InitializeAsync();
        var db=f.Broker.Db;var environment=await db.ComputeEnvironments.SingleAsync();
        environment.State=ComputeLifecycleState.Ready;environment.ProviderResourceId=Guid.NewGuid().ToString("D");
        (await db.ComputeOperations.SingleAsync()).Status="Completed";await db.SaveChangesAsync();
        await ComputeWorkloadTests.GrantAsync(f.Broker,InfrastructureActions.Execute);
        if(scenario!="absent") await GrantRestore(f.Broker,scenario=="registry-denied"?"other.example":"registry.npmjs.org");
        var workload=new ComputeWorkload(new(Guid.NewGuid(),"/bin/echo","/tmp",["restore"],30,8192),NodeDependencies:Dependencies());
        var request=new RequestComputeWorkload(environment.Id,1,"restore",workload);
        if(scenario is "absent" or "registry-denied") {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization,f.Broker.Installation,request,default));return;
        }
        var operation=await f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization,f.Broker.Installation,request,default);
        if(scenario=="revoked-before-dispatch") {
            (await db.ScopedActionGrants.SingleAsync(x=>x.Action==InfrastructureActions.Outbound)).RevokedAt=f.Time.Now;await db.SaveChangesAsync();
            Assert.Null(await f.Authorizer().ClaimAsync(operation.Id,default));return;
        }
        var packet=(await f.Authorizer().ClaimAsync(operation.Id,default))!;var claim=f.Signing.Claims.Last();
        Assert.Equal(new[]{InfrastructureActions.Execute,InfrastructureActions.Outbound},claim.Grants.Select(g=>g.Action));
        var verifier=new ComputeDispatchVerifier(new(claim.OrganizationId,claim.NodeId,claim.ProviderId,await f.Signing.GetIdentityAsync(default)),
            new(claim.ProviderId,["ubuntu-clean"],[InfrastructureActions.Execute],[ComputeNetworkMode.None],false,false),
            new(4,8192,40960),new Dictionary<string,ComputeTemplate>(),f.Time);
        Assert.NotNull(verifier.Verify(packet).Workload!.NodeDependencies);
        Assert.Throws<UnauthorizedAccessException>(()=>verifier.Verify(packet with { Workload=workload with {NodeDependencies=Dependencies([4])}}));
        var constraints=JsonSerializer.Deserialize<ComputeGrantConstraints>((await db.ScopedActionGrants.SingleAsync(x=>x.Action==InfrastructureActions.Outbound)).ConstraintsJson,ComputeProtocol.Json)!;
        var spec=JsonSerializer.Deserialize<ComputeSpecification>((await db.ComputeEnvironments.SingleAsync()).SpecificationJson,ComputeProtocol.Json)!;
        Assert.False(constraints.Allows(spec with {Network=new(ComputeNetworkMode.OutboundOnly,true)},0));
    }

    [Fact]
    public async Task Active_restore_stops_when_recorded_grants_are_revoked_or_the_generation_changes()
    {
        await using var f=new ComputeBrokerTests.Fixture();await f.SeedAsync();var view=await ComputeWorkloadTests.ReadyAsync(f);
        await ComputeWorkloadTests.GrantAsync(f,InfrastructureActions.Execute);await GrantRestore(f,"registry.npmjs.org");
        var operation=await f.Broker.SubmitWorkloadAsync(f.Organization,f.Installation,new(view.Id,1,"restore",new(
            new(Guid.NewGuid(),"/bin/echo","/tmp",[],30,8192),NodeDependencies:Dependencies())),default);
        (await f.Db.ComputeOperations.SingleAsync(x=>x.Id==operation.Id)).Status="Dispatching";await f.Db.SaveChangesAsync();
        var environment=await f.Db.ComputeEnvironments.SingleAsync();using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust=new ComputeResultTests.Trust(new("node",f.Organization,environment.ProviderNodeId!.Value,environment.ProviderId!,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var access=new ComputeDependencyAccess(f.Db,new(trust,new ComputeBrokerTests.Clock()),f.Broker,new ComputeBrokerTests.Clock());
        var now=new ComputeBrokerTests.Clock().GetUtcNow();var proof=new ComputeProviderWorkRequest(f.Organization,environment.ProviderNodeId.Value,environment.ProviderId!,Guid.NewGuid(),now,now.AddMinutes(1),operation.Id);
        var json=JsonSerializer.Serialize(proof,ComputeProtocol.Json);var signed=new SignedComputeProviderWorkRequest("node",json,Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json),HashAlgorithmName.SHA256)));
        Assert.True((await access.AuthorizeAsync(signed,default)).Allowed);
        environment.Generation++;await f.Db.SaveChangesAsync();Assert.False((await access.AuthorizeAsync(signed,default)).Allowed);
        environment.Generation--;var grant=await f.Db.ScopedActionGrants.SingleAsync(x=>x.Action==InfrastructureActions.Outbound);grant.RevokedAt=now;await f.Db.SaveChangesAsync();
        Assert.False((await access.AuthorizeAsync(signed,default)).Allowed);
    }

    [Theory]
    [InlineData("success",null)] [InlineData("denied","dependency-authority-denied")]
    [InlineData("revoked","dependency-authority-denied")] [InlineData("tampered","dependency-integrity-failed")]
    [InlineData("oversize","dependency-fetch-failed")]
    public async Task Provider_verifies_bytes_before_guest_mutation_and_never_executes_host_package_code(string scenario,string? expected)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var commands=new List<ComputeGuestCommand>();var servers=new List<Task>();var authorization=0;var downloads=0;
        async Task<Stream> Connect(CancellationToken token) {
            var name="csweet-npm-test-"+Guid.NewGuid().ToString("N");
            var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            servers.Add(Task.Run(async()=> {await using(server) {
                await server.WaitForConnectionAsync(timeout.Token);
                var request=await ComputeGuestWire.ReadAsync<ComputeGuestWireRequest>(server,ComputeGuestWire.MaximumRequestBytes,timeout.Token);
                commands.Add(request.Command!);
                var exit = request.Command!.Arguments.Any(a => a.Contains("test -f ", StringComparison.Ordinal)) ? 1 : 0;
                await ComputeGuestWire.WriteAsync(server,new ComputeGuestWireResult(1,request.Challenge,request.Command!.RequestId,exit,false,[],[],false),ComputeGuestWire.MaximumResultBytes,timeout.Token);
            }},timeout.Token));
            var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await client.ConnectAsync(token);return client;
        }
        var command=new ComputeGuestCommand(Guid.NewGuid(),"/bin/echo","/tmp",["final-install"],30,8192);
        var result=await ComputeNodeDependencyInstaller.ExecuteAsync(Dependencies(),command,Guid.NewGuid(),Connect,
            _=>Task.FromResult(scenario!="denied" && !(scenario=="revoked" && ++authorization>1)),timeout.Token,
            (_,_)=> {downloads++;return Task.FromResult(scenario=="oversize"?new byte[ComputeNodeDependencies.MaximumPackageBytes+1]:scenario=="tampered"?new byte[]{4,5}:new byte[]{1,2,3});});
        await Task.WhenAll(servers);Assert.Equal(expected,result.ErrorCode);
        if(expected is not null) {Assert.Empty(commands);Assert.Null(result.Command);}
        else {Assert.Equal(command.RequestId,result.Command!.RequestId);Assert.Equal(command.RequestId,commands.Last().RequestId);
            Assert.Equal(command.Executable,commands.Last().Executable);Assert.Equal(command.Arguments,commands.Last().Arguments);
            Assert.Contains(commands,c=>c.Arguments.Any(a=>a.Contains("npm cache add --offline --ignore-scripts")));
            Assert.Contains(commands,c=>c.Arguments.Any(a=>a.Contains(Convert.ToBase64String(new byte[]{1,2,3}))));}
        Assert.Equal(scenario=="denied"?0:1,downloads);
    }

    [Theory]
    [InlineData("chunk-reply-lost")]
    [InlineData("chunk-all-replies-lost")]
    [InlineData("revoked-before-retry")]
    [InlineData("final-reply-lost")]
    public async Task Lost_chunk_replies_repeat_only_identical_fixed_offset_bytes_with_fresh_authority(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bytes = Enumerable.Range(0, 36010).Select(i => (byte)(i % 251)).ToArray();
        var written = new byte[bytes.Length]; var servers = new List<Task>(); var writes = 0; var finals = 0; var revoked = false;
        async Task<Stream> Connect(CancellationToken token)
        {
            var name = "csweet-npm-loss-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            servers.Add(Task.Run(async () => { await using (server) {
                await server.WaitForConnectionAsync(timeout.Token);
                var request = await ComputeGuestWire.ReadAsync<ComputeGuestWireRequest>(server, ComputeGuestWire.MaximumRequestBytes, timeout.Token);
                var script = request.Command!.Executable == "/bin/sh" ? request.Command.Arguments[1] : request.Command.Arguments.Single();
                if (script.Contains("| dd of=", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(">>", script);
                    var encoded = System.Text.RegularExpressions.Regex.Match(script, "printf '%s' '([^']+)'").Groups[1].Value;
                    var blockSize = int.Parse(System.Text.RegularExpressions.Regex.Match(script, " bs=([0-9]+) ").Groups[1].Value);
                    Assert.Equal(18000, blockSize);
                    var offset = blockSize * int.Parse(System.Text.RegularExpressions.Regex.Match(script, " seek=([0-9]+) ").Groups[1].Value);
                    Convert.FromBase64String(encoded).CopyTo(written, offset); writes++;
                    if (scenario == "chunk-all-replies-lost" || writes == 1 && scenario is "chunk-reply-lost" or "revoked-before-retry")
                    { revoked = scenario == "revoked-before-retry"; return; }
                }
                if (request.Command.Executable == "/bin/echo") { finals++; if (scenario == "final-reply-lost") return; }
                var exit = script.Contains("test -f ", StringComparison.Ordinal) ? 1 : 0;
                await ComputeGuestWire.WriteAsync(server, new ComputeGuestWireResult(1, request.Challenge, request.Command.RequestId, exit, false, [], [], false), ComputeGuestWire.MaximumResultBytes, timeout.Token);
            } }, timeout.Token));
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); await client.ConnectAsync(token); return client;
        }
        var command = new ComputeGuestCommand(Guid.NewGuid(), "/bin/echo", "/tmp", ["final-install"], 30, 8192);
        Task<ComputeWorkloadResult> Run() => ComputeNodeDependencyInstaller.ExecuteAsync(Dependencies(bytes), command, Guid.NewGuid(), Connect,
            _ => Task.FromResult(!revoked), timeout.Token, (_, _) => Task.FromResult(bytes));
        if (scenario is "chunk-all-replies-lost" or "final-reply-lost") await Assert.ThrowsAnyAsync<IOException>(Run);
        else { var result = await Run(); Assert.Equal(scenario == "revoked-before-retry" ? "dependency-authority-denied" : null, result.ErrorCode); }
        await Task.WhenAll(servers);
        Assert.Equal(scenario == "chunk-reply-lost" ? 4 : scenario == "revoked-before-retry" ? 1 : 3, writes);
        Assert.Equal(scenario is "chunk-reply-lost" or "final-reply-lost" ? 1 : 0, finals);
        if (finals == 1) Assert.Equal(bytes, written);
    }

    [Theory]
    [InlineData("verified", true)]
    [InlineData("partial", false)]
    [InlineData("different-bytes", false)]
    public async Task Fresh_restore_reuses_only_verified_complete_tarballs_from_interrupted_transfers(string scenario, bool reusable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bytes = Enumerable.Range(0, 36010).Select(i => (byte)(i % 251)).ToArray();
        var dependency = Dependencies(bytes); var file = dependency.GuestDirectory + "/packages/" + ComputeNodeDependencies.FileName(dependency.Packages[0]);
        var guest = scenario == "verified" ? bytes.ToArray() : scenario == "partial" ? bytes[..18000] : new byte[bytes.Length];
        var servers = new List<Task>(); var writes = 0; var truncations = 0; var downloads = 0; var final = 0; var checks = 0;
        async Task<Stream> Connect(CancellationToken token)
        {
            var name = "csweet-npm-cache-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            servers.Add(Task.Run(async () => { await using (server) {
                await server.WaitForConnectionAsync(timeout.Token);
                var request = await ComputeGuestWire.ReadAsync<ComputeGuestWireRequest>(server, ComputeGuestWire.MaximumRequestBytes, timeout.Token);
                var script = request.Command!.Arguments.Last(); var exit = 0;
                if (script.Contains("test -f ", StringComparison.Ordinal))
                {
                    checks++;
                    Assert.Contains($"test ! -L {file}", script);
                    Assert.Contains($"-eq {bytes.Length}", script);
                    Assert.Contains(Convert.ToHexStringLower(SHA512.HashData(bytes)), script);
                    exit = guest.SequenceEqual(bytes) ? 0 : 1;
                }
                if (script.Contains(": > ", StringComparison.Ordinal)) { truncations++; guest = new byte[bytes.Length]; }
                if (script.Contains("| dd of=", StringComparison.Ordinal))
                {
                    var encoded = System.Text.RegularExpressions.Regex.Match(script, "printf '%s' '([^']+)'").Groups[1].Value;
                    var blockSize = int.Parse(System.Text.RegularExpressions.Regex.Match(script, " bs=([0-9]+) ").Groups[1].Value);
                    var offset = blockSize * int.Parse(System.Text.RegularExpressions.Regex.Match(script, " seek=([0-9]+) ").Groups[1].Value);
                    Convert.FromBase64String(encoded).CopyTo(guest, offset); writes++;
                }
                if (script.Contains("npm cache add", StringComparison.Ordinal)) Assert.Equal(bytes, guest);
                if (request.Command.Executable == "/bin/echo") final++;
                await ComputeGuestWire.WriteAsync(server, new ComputeGuestWireResult(1, request.Challenge, request.Command.RequestId, exit, false, [], [], false), ComputeGuestWire.MaximumResultBytes, timeout.Token);
            } }, timeout.Token));
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); await client.ConnectAsync(token); return client;
        }
        var result = await ComputeNodeDependencyInstaller.ExecuteAsync(dependency, new(Guid.NewGuid(), "/bin/echo", "/tmp", ["final-install"], 30, 8192),
            Guid.NewGuid(), Connect, _ => Task.FromResult(true), timeout.Token, (_, _) => { downloads++; return Task.FromResult(bytes); });
        await Task.WhenAll(servers);
        Assert.Null(result.ErrorCode); Assert.Equal(1, final); Assert.Equal(1, checks); Assert.Equal(1, downloads);
        Assert.Equal(reusable ? 0 : 1, truncations); Assert.Equal(reusable ? 0 : 3, writes); Assert.Equal(bytes, guest);
    }

    private static async Task GrantRestore(ComputeBrokerTests.Fixture f,string registry)
    {
        await ComputeWorkloadTests.GrantAsync(f,InfrastructureActions.Outbound);
        var grant=await f.Db.ScopedActionGrants.SingleAsync(x=>x.Action==InfrastructureActions.Outbound);
        var constraints=JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson,ComputeProtocol.Json)!;
        grant.ConstraintsJson=JsonSerializer.Serialize(constraints with {AllowOutbound=true,AllowedDependencyRegistries=[registry]},ComputeProtocol.Json);
        await f.Db.SaveChangesAsync();f.Db.ChangeTracker.Clear();
    }
}
