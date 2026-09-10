using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
namespace CSweet.UnitTests;

public sealed class WebHostRegistryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public ECDsa Identity { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Authority { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid ProviderId { get; } = Guid.NewGuid();
        public OrganizationUser Owner { get; } = new()
        {
            Id = Guid.NewGuid(), ApplicationUserId = Guid.NewGuid(), DisplayName = "Owner",
            PermissionLevel = OrganizationPermissionLevel.Owner, EmployeeType = EmployeeType.Human
        };
        public WebHostRegistryOptions Options { get; } = new() { ControlPlaneId = Guid.NewGuid(), AuthorizationVerificationKeyId = "test-only" };
        public WebHostRegistryService Service => new(Db, new WebPreviewGrantService(Db, new Clock()),
            Microsoft.Extensions.Options.Options.Create(Options), new Clock());
        public RegisterWebHost Request { get; private set; } = null!;
        public async Task SeedAsync()
        {
            Owner.OrganizationId = OrganizationId;
            Db.CoreOrganizationUsers.Add(Owner);
            var version = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = WebPreviewGrantService.PluginId };
            Db.AgentInstallations.Add(new()
            {
                Id = ProviderId, BusinessId = OrganizationId.ToString("D"), PackageVersion = version, PackageVersionId = version.Id
            });
            Options.AuthorizationVerificationPublicKeyBase64 = Convert.ToBase64String(Authority.ExportSubjectPublicKeyInfo());
            Request = new(Guid.NewGuid(), ProviderId, "Private demo host",
                Convert.ToBase64String(Identity.ExportSubjectPublicKeyInfo()), ResourceBudget.Default, Now.AddDays(7));
            await Db.SaveChangesAsync();
        }
        public Task<WebHostBootstrap> RegisterAsync() => Service.RegisterAsync(OrganizationId, Owner.ApplicationUserId!.Value, Request, default);
        public SignedWebHostMessage Message(WebHostBootstrap bootstrap, long sequence = 1, WebHostHeartbeat? heartbeat = null)
        {
            heartbeat ??= new(bootstrap.Enrollment.Id, Now, ResourceBudget.Default,
                [new("webhost-hyperv-gen2", "0.1.0", "sha256:" + new string('a',64), true, Now.AddDays(1), true, null)]);
            var body = JsonSerializer.Serialize(heartbeat, PreviewJson.Options);
            return Sign(new(1, bootstrap.ControlPlaneId, bootstrap.Enrollment.Id, Guid.NewGuid(), sequence,
                "heartbeat", body, WorkloadAuthorizationEnvelope.Digest(body), "", Now, Now.AddSeconds(60)));
        }
        public SignedWebHostMessage Sign(SignedWebHostMessage message) => message with
        { SignatureBase64 = Convert.ToBase64String(Identity.SignData(message.Payload(), HashAlgorithmName.SHA256)) };
        public async ValueTask DisposeAsync() { Identity.Dispose(); Authority.Dispose(); await Db.DisposeAsync(); }
    }

    [Fact] public async Task Registration_requires_current_human_owner_and_installed_optional_provider()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RegisterAsync(f.OrganizationId, Guid.NewGuid(), f.Request, default));
        f.Owner.EmployeeType = EmployeeType.Agent; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.RegisterAsync());
        f.Owner.EmployeeType = EmployeeType.Human; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RegisterAsync(Guid.NewGuid(), f.Owner.ApplicationUserId!.Value, f.Request, default));
        (await f.Db.AgentInstallations.SingleAsync()).IsEnabled = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.RegisterAsync());
        Assert.Empty(await f.Db.WebHostRegistrations.ToListAsync());
    }

    [Fact] public async Task Registration_is_idempotent_but_cannot_rebind_terms_or_reuse_identity_keys()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var first = await f.RegisterAsync();
        Assert.Equal(first, await f.RegisterAsync());
        Assert.Equal(f.OrganizationId, first.OrganizationId);
        Assert.Equal(f.Options.AuthorizationVerificationPublicKeyBase64, first.Enrollment.VerificationPublicKeyBase64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RegisterAsync(f.OrganizationId, f.Owner.ApplicationUserId!.Value,
            f.Request with { DisplayName = "Different" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RegisterAsync(f.OrganizationId, f.Owner.ApplicationUserId!.Value,
            f.Request with { RequestId = Guid.NewGuid() }, default));
        Assert.Single(await f.Db.WebHostRegistrations.ToListAsync());
    }

    [Fact] public async Task Heartbeat_consumes_sequence_and_does_not_trust_self_reported_certification()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var host = await f.RegisterAsync();
        var message = f.Message(host);
        var receipt = await f.Service.HeartbeatAsync(message, default);
        Assert.False(receipt.ExecutionReady);
        Assert.Equal("CertifiedDispatchNotConfigured", receipt.ReadinessReason);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.HeartbeatAsync(message, default));
        await f.Service.HeartbeatAsync(f.Message(host, 3), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.HeartbeatAsync(f.Message(host, 2), default));
        var view = Assert.Single(await f.Service.ListAsync(f.OrganizationId, f.Owner.ApplicationUserId!.Value, default));
        Assert.True(view.Connected); Assert.False(view.ExecutionReady);
        Assert.Equal(3, (await f.Db.WebHostRegistrations.SingleAsync()).LastSequence);
    }

    [Fact] public async Task Rejects_wrong_control_plane_host_signature_expiry_and_body_without_consuming_sequence()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var host = await f.RegisterAsync(); var valid = f.Message(host);
        var invalid = new[]
        {
            f.Sign(valid with { ControlPlaneId = Guid.NewGuid() }),
            f.Sign(valid with { WebHostId = Guid.NewGuid() }),
            f.Sign(valid with { IssuedAt = Now.AddSeconds(-60), ExpiresAt = Now }),
            f.Sign(valid with { ExpiresAt = Now.AddMinutes(5) }),
            f.Sign(valid with { Action = "office-heartbeat" }),
            valid with { BodyJson = "{}" },
            valid with { SignatureBase64 = Convert.ToBase64String(new byte[64]) }
        };
        foreach (var message in invalid)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.HeartbeatAsync(message, default));
        Assert.Equal(0, (await f.Db.WebHostRegistrations.SingleAsync()).LastSequence);
        await f.Service.HeartbeatAsync(valid, default);
    }

    [Fact] public async Task Revocation_disables_identity_and_access_but_retains_teardown_work()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var host = await f.RegisterAsync();
        var job = new WebPreviewJobRecord
        {
            Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, WebHostId = host.Enrollment.Id,
            Phase = "Ready", AccessReference = "private-session", ExpiresAt = Now.AddHours(1)
        };
        f.Db.WebPreviewJobs.Add(job); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RevokeAsync(f.OrganizationId, host.Enrollment.Id, Guid.NewGuid(), default));
        await f.Service.RevokeAsync(f.OrganizationId, host.Enrollment.Id, f.Owner.ApplicationUserId!.Value, default);
        await f.Service.RevokeAsync(f.OrganizationId, host.Enrollment.Id, f.Owner.ApplicationUserId!.Value, default);
        Assert.Equal("Stopping", job.Phase); Assert.Equal("HostRevoked", job.FailureCode); Assert.Null(job.AccessReference);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.HeartbeatAsync(f.Message(host), default));
        var view = Assert.Single(await f.Service.ListAsync(f.OrganizationId, f.Owner.ApplicationUserId!.Value, default));
        Assert.False(view.Connected); Assert.Equal("Revoked", view.Status);
    }

    [Fact] public async Task Concurrent_headquarters_contexts_cannot_both_consume_a_sequence()
    {
        await using var f = new Fixture(); await f.SeedAsync(); var host = await f.RegisterAsync();
        var options = (DbContextOptions<CSweetDbContext>)f.Db.GetService<IDbContextOptions>();
        await using var replica = new CSweetDbContext(options);
        await replica.WebHostRegistrations.SingleAsync(); // Hold the same revision read by the other replica.
        var other = new WebHostRegistryService(replica, new WebPreviewGrantService(replica, new Clock()),
            Microsoft.Extensions.Options.Options.Create(f.Options), new Clock());
        var message = f.Message(host);
        await f.Service.HeartbeatAsync(message, default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.HeartbeatAsync(message, default));
        replica.ChangeTracker.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.HeartbeatAsync(message, default));
        await other.HeartbeatAsync(f.Message(host, 2), default);
        Assert.Equal(2, (await replica.WebHostRegistrations.SingleAsync()).LastSequence);
    }

    [Fact] public async Task Rejects_excess_capacity_and_disabled_provider_reports()
    {
        await using var f = new Fixture(); await f.SeedAsync(); var host = await f.RegisterAsync();
        var tooLarge = new WebHostHeartbeat(host.Enrollment.Id, Now, ResourceBudget.Default with { CpuCount = 100 },
            [new("provider", "0.1.0", "sha256:" + new string('a',64), false, null, false, null)]);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.HeartbeatAsync(f.Message(host, heartbeat: tooLarge), default));
        Assert.Equal(0, (await f.Db.WebHostRegistrations.SingleAsync()).LastSequence);
        (await f.Db.AgentInstallations.SingleAsync()).IsEnabled = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.HeartbeatAsync(f.Message(host), default));
    }
}
