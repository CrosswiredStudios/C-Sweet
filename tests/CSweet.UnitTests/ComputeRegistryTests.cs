using System.Security.Cryptography;
using CSweet.Api.Compute;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class ComputeRegistryTests
{
    private static ComputeTemplate Template => new("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), []);
    private static string PublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
    private static ComputeRegistry Registry(ComputeBrokerTests.Fixture f) => new(f.Db, new ComputeBrokerTests.Clock(), new AuditExecutionContextAccessor());

    [Fact]
    public async Task Revocation_and_rotation_are_current_and_organization_scoped()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var registry = Registry(f);
        var node = await registry.PutNodeAsync(f.Organization, Guid.NewGuid(), 0, "Build node", "hyper-v", "key-one", PublicKey(), true, default);
        Assert.NotNull(await registry.ResolveAsync(f.Organization, node.Id, "key-one", default));
        Assert.Null(await registry.ResolveAsync(Guid.NewGuid(), node.Id, "key-one", default));
        await registry.PutNodeAsync(f.Organization, node.Id, 1, node.Name, node.ProviderId, "key-two", PublicKey(), true, default);
        Assert.Null(await registry.ResolveAsync(f.Organization, node.Id, "key-one", default));
        Assert.NotNull(await registry.ResolveAsync(f.Organization, node.Id, "key-two", default));
        await registry.PutNodeAsync(f.Organization, node.Id, 2, node.Name, node.ProviderId, node.KeyId, node.VerificationPublicKeyBase64, false, default);
        Assert.Null(await registry.ResolveAsync(f.Organization, node.Id, "key-two", default));
        Assert.Equal(3, await f.Db.ComputeAuditOutbox.CountAsync());
    }

    [Fact]
    public async Task Templates_are_immutable_and_placement_can_fail_over_without_rewriting_existing_environment()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var registry = Registry(f);
        var first = await registry.PutNodeAsync(f.Organization, Guid.NewGuid(), 0, "One", "hyper-v", "key-one", PublicKey(), true, default);
        var second = await registry.PutNodeAsync(f.Organization, Guid.NewGuid(), 0, "Two", "kvm", "key-two", PublicKey(), true, default);
        var template = await registry.PutTemplateAsync(f.Organization, Template, 0, default);
        Assert.Null(await registry.ResolveAsync(f.Organization, Template.Id, default));
        await registry.PutPlacementAsync(f.Organization, template.Id, first.Id, 0, true, default);
        await registry.PutPlacementAsync(f.Organization, template.Id, second.Id, 0, true, default);
        var selected = (await registry.ResolveAsync(f.Organization, Template.Id, default))!;
        Assert.Empty(selected.Template.Features);
        var broker = new ComputeBroker(f.Db, registry, new ComputeBrokerTests.Clock(), f.Ledger);
        await broker.RequestAsync(f.Organization, f.Installation, f.Request, default);
        var chosen = selected.NodeId == first.Id ? first : second;
        await registry.PutNodeAsync(f.Organization, chosen.Id, 1, chosen.Name, chosen.ProviderId, chosen.KeyId,
            chosen.VerificationPublicKeyBase64, false, default);
        Assert.NotEqual(selected.NodeId, (await registry.ResolveAsync(f.Organization, Template.Id, default))!.NodeId);
        Assert.Equal(selected.NodeId, (await f.Db.ComputeEnvironments.SingleAsync()).ProviderNodeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.PutTemplateAsync(f.Organization,
            Template with { ImageDigest = "sha256:" + new string('b', 64) }, 1, default));
        await registry.PutTemplateAsync(f.Organization, Template with { Enabled = false }, 1, default);
        Assert.Null(await registry.ResolveAsync(f.Organization, Template.Id, default));
    }

    [Fact]
    public async Task Wrong_curve_stale_revision_and_cross_organization_placement_are_rejected()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var registry = Registry(f);
        using var weak = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.PutNodeAsync(f.Organization, Guid.NewGuid(), 0, "Wrong curve",
            "hyper-v", "key", Convert.ToBase64String(weak.ExportSubjectPublicKeyInfo()), true, default));
        var node = await registry.PutNodeAsync(f.Organization, Guid.NewGuid(), 0, "Node", "hyper-v", "key", PublicKey(), true, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.PutNodeAsync(f.Organization, node.Id, 0,
            node.Name, node.ProviderId, node.KeyId, node.VerificationPublicKeyBase64, false, default));
        var template = await registry.PutTemplateAsync(f.Organization, Template, 0, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.PutPlacementAsync(Guid.NewGuid(), template.Id, node.Id, 0, true, default));
        Assert.True(node.Enabled);
        Assert.Empty(await f.Db.ComputeTemplatePlacements.ToListAsync());
    }

    [Fact]
    public async Task Every_administration_route_requires_host_administration()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddScoped<ComputeRegistry>();
        builder.Services.AddSingleton<CSweet.Application.Compute.IComputeDispatchSigner>(_ => new ComputeDispatchTests.Signer());
        builder.Services.AddScoped<CSweet.Infrastructure.Persistence.CSweetDbContext>();
        await using var app = builder.Build();
        app.MapComputeAdministrationEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).ToArray();
        Assert.Equal(9, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), x => x.Policy == "HostAdministration"));
    }
}
