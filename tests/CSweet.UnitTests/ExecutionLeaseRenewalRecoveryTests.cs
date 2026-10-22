using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Domain.Setup;
using CSweet.ExecutionGateway;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.ControlPlane;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class ExecutionWorkloadOrchestratorTests
{
    [Theory]
    [InlineData("token-rotation", true)]
    [InlineData("fenced", false)]
    [InlineData("expired", false)]
    [InlineData("revoked-office", false)]
    [InlineData("conflict-budget", false)]
    public async Task LeaseRenewalReloadsAuthorityAfterConcurrentChanges(string race, bool expected)
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.LeaseExpiresAt = Now.AddSeconds(5);
        var other = Assignment(pool.Id, node.Id, 1, 512);
        await using (var seed = new CSweetDbContext(options))
        {
            seed.AddRange(pool, node, assignment, other);
            await seed.SaveChangesAsync();
        }
        var interceptor = new RenewalRace(options, assignment.Id, race);
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>(options)
            .AddInterceptors(interceptor).Options);
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));

        Assert.Equal(expected, await scheduler.RenewLeaseAsync(node.Id, assignment.Id, assignment.FencingEpoch));
        Assert.Equal(race == "conflict-budget" ? 3 : 1, interceptor.Conflicts);
        Assert.True(await scheduler.RenewLeaseAsync(node.Id, other.Id, other.FencingEpoch) == (race != "revoked-office"));
        await using var verify = new CSweetDbContext(options);
        var saved = await verify.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.Equal(expected ? Now.AddSeconds(60) : race == "expired" ? Now.AddSeconds(-1) : Now.AddSeconds(5), saved.LeaseExpiresAt);
    }

    [Fact]
    public async Task GatewayKeepsServingOtherAssignmentsAfterRenewalConflict()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=renewal-recovery", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        node.CertificateThumbprint = certificate.Thumbprint;
        node.CertificateSerialNumber = certificate.SerialNumber;
        node.CertificateExpiresAt = Now.AddYears(1);
        var first = Assignment(pool.Id, node.Id, 1, 512);
        var second = Assignment(pool.Id, node.Id, 1, 512);
        await using (var seed = new CSweetDbContext(options))
        {
            seed.AddRange(pool, node, first, second);
            await seed.SaveChangesAsync();
        }
        var race = new RenewalRace(options, first.Id, "conflict-budget");
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>(options)
            .AddInterceptors(race).Options);
        var clock = new MutableTimeProvider(Now);
        var gateway = new OfficeGatewayService(db, new ExecutionWorkloadOrchestrator(db, clock),
            null!, null!, null!, null!, clock, NullLogger<OfficeGatewayService>.Instance);
        var messages = new[] { first, second }.Select(x => new OfficeControlMessage
        {
            OfficeId = node.Id.ToString("D"), SessionEpoch = node.SessionEpoch, ProtocolVersion = "1.0",
            LeaseRenewal = new AssignmentLeaseRenewal { AssignmentId = x.Id.ToString("D"), FencingEpoch = x.FencingEpoch }
        }).ToArray();
        var http = new DefaultHttpContext();
        http.Connection.ClientCertificate = certificate;
        var replies = new StopReplyStream();

        await gateway.Connect(new RenewalRequestStream(messages), replies, new StopCallContext(http));

        Assert.Equal(first.Id.ToString("D"), Assert.Single(replies.Messages).Fence.AssignmentId);
        await using var verify = new CSweetDbContext(options);
        Assert.Equal(Now.AddSeconds(60), (await verify.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == second.Id)).LeaseExpiresAt);
    }

    private sealed class RenewalRequestStream(OfficeControlMessage[] messages) : IAsyncStreamReader<OfficeControlMessage>
    {
        private int index = -1;
        public OfficeControlMessage Current => messages[index];
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(++index < messages.Length);
    }

    private sealed class RenewalRace(DbContextOptions<CSweetDbContext> options, Guid assignmentId, string race)
        : SaveChangesInterceptor
    {
        public int Conflicts { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if ((Conflicts > 0 && race != "conflict-budget") ||
                !eventData.Context!.ChangeTracker.Entries<ExecutionWorkloadAssignment>()
                    .Any(x => x.Entity.Id == assignmentId && x.State == EntityState.Modified)) return result;
            await using var concurrent = new CSweetDbContext(options);
            var assignment = await concurrent.ExecutionWorkloadAssignments.Include(x => x.ExecutionNode)
                .SingleAsync(x => x.Id == assignmentId, cancellationToken);
            assignment.AssignmentTokenHash = Guid.NewGuid().ToString("N");
            if (race == "fenced") assignment.FencingEpoch++;
            if (race == "expired") assignment.LeaseExpiresAt = Now.AddSeconds(-1);
            if (race == "revoked-office") assignment.ExecutionNode!.Status = ExecutionNodeStatus.Revoked;
            await concurrent.SaveChangesAsync(cancellationToken);
            Conflicts++;
            return result;
        }
    }
}
