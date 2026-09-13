using CSweet.Compute.Contracts;
using CSweet.Application.Compute;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    private static ComputeSpecification Clean => new("windows", "x64", "windows-clean", new(2, 4096, 20480), 600);
    private static ComputeTemplate Template => new("windows-clean", "windows", "x64", "sha256:" + new string('a', 64), new HashSet<string>());
    private static ComputeGrantConstraints Limits => new(1, new(4, 8192, 40960), 2, 3600,
        new HashSet<string> { "windows" }, new HashSet<string> { "x64" }, new HashSet<string> { "windows-clean" });
    private static ComputeActionAuthorization Grant(string action) => new(Guid.NewGuid(), 1, action, Now.AddHours(1));

    [Fact]
    public void Clean_OS_provision_requires_neither_Docker_nor_build_artifact_or_network()
    {
        var result = ComputePolicy.Evaluate(Clean, Template, Limits, [Grant(InfrastructureActions.Provision)], 0, Now);
        Assert.True(result.Allowed);
        Assert.Empty(Template.Features);
        Assert.Equal(new[] { InfrastructureActions.Provision }, result.Authority.Select(x => x.Action));
        Assert.Equal(ComputeNetworkMode.None, Clean.NetworkPolicy.Mode);
    }

    [Theory]
    [InlineData(ComputeNetworkMode.Private, false, false, InfrastructureActions.PrivateNetwork)]
    [InlineData(ComputeNetworkMode.OutboundOnly, true, false, InfrastructureActions.Outbound)]
    [InlineData(ComputeNetworkMode.Inbound, false, false, InfrastructureActions.Inbound)]
    [InlineData(ComputeNetworkMode.Inbound, false, true, InfrastructureActions.PublishPort)]
    public void Compute_provision_does_not_grant_network_authority(ComputeNetworkMode mode,
        bool outbound, bool publish, string missing)
    {
        var spec = Clean with { Network = new(mode, outbound, publish, publish ? [443] : []) };
        var limits = Limits with { AllowOutbound = true, AllowPublicEndpoint = true, AllowedPublishedPorts = new HashSet<int> { 443 } };
        var authority = ComputePolicy.RequiredProvisionActions(spec).Where(x => x != missing).Select(Grant).ToArray();
        Assert.Equal("ActionGrantRequired", ComputePolicy.Evaluate(spec, Template, limits, authority, 0, Now).FailureCode);
        Assert.True(ComputePolicy.Evaluate(spec, Template, limits, [.. authority, Grant(missing)], 0, Now).Allowed);
    }

    [Fact]
    public void Persistent_compute_requires_both_explicit_policy_and_separate_action()
    {
        var spec = Clean with { Persistence = ComputePersistence.Persistent };
        var provision = Grant(InfrastructureActions.Provision);
        var persist = Grant(InfrastructureActions.Persist);
        Assert.False(ComputePolicy.Evaluate(spec, Template, Limits, [provision, persist], 0, Now).Allowed);
        Assert.False(ComputePolicy.Evaluate(spec, Template, Limits with { AllowPersistent = true }, [provision], 0, Now).Allowed);
        Assert.True(ComputePolicy.Evaluate(spec, Template, Limits with { AllowPersistent = true }, [provision, persist], 0, Now).Allowed);
    }

    [Theory]
    [InlineData(5, 4096, 20480, 0)]
    [InlineData(2, 8193, 20480, 0)]
    [InlineData(2, 4096, 40961, 0)]
    [InlineData(2, 4096, 20480, 1)]
    [InlineData(0, 4096, 20480, 0)]
    [InlineData(2, -1, 20480, 0)]
    public void Resource_constraints_are_enforced_without_overflow(int cpu, long memory, long disk, int gpu) =>
        Assert.False(ComputePolicy.Evaluate(Clean with { Resources = new(cpu, memory, disk, gpu) },
            Template, Limits, [Grant(InfrastructureActions.Provision)], 0, Now).Allowed);

    [Fact]
    public void Quota_counts_unconfirmed_failed_and_stopped_environments()
    {
        var failed = new ComputeEnvironment { State = ComputeLifecycleState.Failed };
        var stopped = new ComputeEnvironment { State = ComputeLifecycleState.Stopped };
        var count = new[] { failed, stopped }.Count(x => x.HoldsReservation);
        Assert.False(ComputePolicy.Evaluate(Clean, Template, Limits, [Grant(InfrastructureActions.Provision)], count, Now).Allowed);
        failed.TeardownConfirmedAt = Now;
        Assert.False(failed.HoldsReservation);
    }

    [Fact]
    public void Template_identity_and_enabled_state_are_authoritative()
    {
        foreach (var template in new[] { Template with { Enabled = false }, Template with { OperatingSystem = "linux" },
            Template with { Architecture = "arm64" }, Template with { ImageDigest = "unverified" }, Template with { Id = "ubuntu-clean" } })
            Assert.Equal("TemplateUnavailable", ComputePolicy.Evaluate(Clean, template, Limits,
                [Grant(InfrastructureActions.Provision)], 0, Now).FailureCode);
    }

    [Fact]
    public void Separate_action_does_not_override_ports_or_network_constraints()
    {
        var spec = Clean with { Network = new(ComputeNetworkMode.Inbound, true, true, [443, 22]) };
        var authority = ComputePolicy.RequiredProvisionActions(spec).Select(Grant).ToArray();
        var limits = Limits with { AllowOutbound = true, AllowPublicEndpoint = true, AllowedPublishedPorts = new HashSet<int> { 443 } };
        Assert.False(ComputePolicy.Evaluate(spec, Template, limits, authority, 0, Now).Allowed);
        Assert.False(ComputePolicy.Evaluate(spec with { Network = new(ComputeNetworkMode.None, PublishedPorts: [443]) },
            Template, limits, authority, 0, Now).Allowed);
    }

    [Theory]
    [InlineData("C:\\images\\windows.vhdx")]
    [InlineData("../windows")]
    [InlineData("https://untrusted/image")]
    [InlineData("windows;command")]
    public void Template_request_cannot_supply_host_paths_or_commands(string id) =>
        Assert.False((Clean with { TemplateId = id }).IsValid);

    [Fact]
    public void Grant_must_cover_the_entire_lease_and_current_revision()
    {
        var grant = Grant(InfrastructureActions.Provision);
        foreach (var invalid in new[] { grant with { ExpiresAt = Now.AddSeconds(599) }, grant with { Revision = 0 },
            grant with { GrantId = Guid.Empty } })
            Assert.False(ComputePolicy.Evaluate(Clean, Template, Limits, [invalid], 0, Now).Allowed);
        Assert.False(ComputePolicy.Evaluate(Clean with { LifetimeSeconds = 3601 }, Template, Limits, [grant], 0, Now).Allowed);
        Assert.False(ComputePolicy.Evaluate(Clean, Template, Limits with { Version = 2 }, [grant], 0, Now).Allowed);
    }

    [Theory]
    [InlineData(ComputePersistence.Ephemeral, true)]
    [InlineData(ComputePersistence.Persistent, false)]
    public void Automatic_cleanup_distinguishes_ephemeral_from_durable_infrastructure(ComputePersistence persistence, bool expected)
    {
        var environment = new ComputeEnvironment { Persistence = persistence, LeaseExpiresAt = Now.AddMinutes(-1), LastUsedAt = Now.AddDays(-10) };
        Assert.Equal(expected, environment.ShouldRequestAutomaticDestruction(Now, false));
        environment.LeaseExpiresAt = Now.AddHours(1);
        Assert.Equal(expected, environment.ShouldRequestAutomaticDestruction(Now, true));
        Assert.False(environment.ShouldRequestAutomaticDestruction(Now, false));
        environment.TeardownConfirmedAt = Now;
        Assert.False(environment.ShouldRequestAutomaticDestruction(Now, true));
    }
}
