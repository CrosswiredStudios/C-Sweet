using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeRuntimeVerifierTests
{
    private static async Task<ComputeDispatchVerifier> Verifier(ComputeDispatchTests.Fixture fixture,
        ComputeResources? maximum = null, ComputeTemplate? overrideImage = null)
    {
        var claim = fixture.Signing.Claims[0];
        var template = overrideImage ?? new("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), []);
        return new(new(claim.OrganizationId, claim.NodeId, claim.ProviderId, await fixture.Signing.GetIdentityAsync(default)),
            new(claim.ProviderId, ["ubuntu-clean"], [InfrastructureActions.Provision, InfrastructureActions.Start,
                InfrastructureActions.Stop, InfrastructureActions.Restart, InfrastructureActions.Destroy],
                [ComputeNetworkMode.None, ComputeNetworkMode.OutboundOnly], false, false),
            maximum ?? new(4, 8192, 40960), new Dictionary<string, ComputeTemplate> { [template.Id] = template }, fixture.Time);
    }

    [Fact]
    public async Task Independent_runtime_accepts_signed_clean_template_without_core_services()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        var packet = (await f.Authorizer().ClaimAsync(f.OperationId, default))!;
        var verified = (await Verifier(f)).Verify(packet);
        Assert.Equal(ComputeDispatchMode.Execute, verified.Authorization.Mode);
        Assert.Empty(verified.Template!.Features);
        Assert.Equal(packet.Specification.Resources, verified.Specification.Resources);
        var references = typeof(ComputeDispatchVerifier).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(references, x => x is "CSweet.Infrastructure" or "CSweet.Application" or "CSweet.Agent.SDK" || x!.Contains("EntityFramework"));
    }

    [Theory]
    [InlineData("specification")]
    [InlineData("signature")]
    [InlineData("node")]
    [InlineData("expired")]
    [InlineData("network-grant")]
    [InlineData("observe-with-grants")]
    [InlineData("oversized-lifetime")]
    public async Task Provider_rejects_tampering_and_signed_but_forbidden_shapes(string scenario)
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        var packet = (await f.Authorizer().ClaimAsync(f.OperationId, default))!;
        var verifier = await Verifier(f);
        var claim = f.Signing.Claims[0];
        if (scenario == "specification") packet = packet with { Specification = packet.Specification with { Resources = new(64, 65536, 40960) } };
        if (scenario == "signature") packet = packet with { Authorization = packet.Authorization with { SignatureBase64 = Convert.ToBase64String(new byte[64]) } };
        if (scenario == "node") claim = claim with { NodeId = Guid.NewGuid() };
        if (scenario == "expired") f.Time.Now = claim.ExpiresAt;
        if (scenario == "network-grant")
        {
            packet = packet with { Specification = packet.Specification with { Network = new(ComputeNetworkMode.OutboundOnly, true) } };
            claim = claim with { SpecificationDigest = ComputeProtocol.Digest(JsonSerializer.Serialize(packet.Specification, ComputeProtocol.Json)) };
        }
        if (scenario == "observe-with-grants") claim = claim with { Mode = ComputeDispatchMode.Observe };
        if (scenario == "oversized-lifetime") claim = claim with { ExpiresAt = claim.IssuedAt.AddHours(1) };
        if (scenario is "node" or "network-grant" or "observe-with-grants" or "oversized-lifetime")
            packet = packet with { Authorization = await f.Signing.SignAsync(claim, default) };
        Assert.Throws<UnauthorizedAccessException>(() => verifier.Verify(packet));
    }

    [Fact]
    public async Task Local_resource_limit_and_certified_image_are_independent_of_core_signature()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        var packet = (await f.Authorizer().ClaimAsync(f.OperationId, default))!;
        var constrained = await Verifier(f, new(1, 2048, 10240));
        Assert.Throws<UnauthorizedAccessException>(() => constrained.Verify(packet));
        var differentImage = await Verifier(f, overrideImage: packet.Template! with { ImageDigest = "sha256:" + new string('b', 64) });
        Assert.Throws<UnauthorizedAccessException>(() => differentImage.Verify(packet));
    }

    [Fact]
    public async Task Observation_recovery_is_distinct_from_mutation_and_has_no_template_or_grants()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        await f.Authorizer().ClaimAsync(f.OperationId, default);
        f.Time.Now = f.Time.Now.AddSeconds(61);
        var packet = (await f.Authorizer().ClaimAsync(f.OperationId, default))!;
        var verified = (await Verifier(f)).Verify(packet);
        Assert.Equal(ComputeDispatchMode.Observe, verified.Authorization.Mode);
        Assert.Empty(verified.Authorization.Grants);
        Assert.Null(verified.Template);
    }

    [Fact]
    public void Capability_metadata_roundtrips_for_remote_provider_negotiation()
    {
        var capabilities = new ComputeProviderCapabilities("hyper-v", ["ubuntu-clean"], [InfrastructureActions.Provision],
            [ComputeNetworkMode.None], false, false);
        var roundtrip = JsonSerializer.Deserialize<ComputeProviderCapabilities>(JsonSerializer.Serialize(capabilities, ComputeProtocol.Json), ComputeProtocol.Json)!;
        Assert.True(capabilities.TemplateIds.SetEquals(roundtrip.TemplateIds));
        Assert.True(capabilities.Actions.SetEquals(roundtrip.Actions));
        Assert.True(capabilities.NetworkModes.SetEquals(roundtrip.NetworkModes));
    }
}
