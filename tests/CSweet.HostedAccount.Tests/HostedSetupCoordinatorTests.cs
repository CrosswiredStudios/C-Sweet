using System.Net;
using Microsoft.Extensions.Options;

namespace CSweet.HostedAccount.Tests;

public sealed class HostedSetupCoordinatorTests
{
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));

    [Fact]
    public async Task Polls_only_when_due_then_registers_after_approval()
    {
        var issuer = new FakeIssuer { TokenError = "authorization_pending" };
        using var setup = Coordinator(issuer);
        var started = await setup.StartAsync("  Studio Mac  ");
        Assert.Equal(HostedSetupState.Waiting, started.State);
        Assert.Equal("Studio Mac", started.InstallationName);

        await setup.PollAsync(started.AttemptId);
        Assert.Equal(0, issuer.TokenCalls); // interval not reached

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(HostedSetupState.Waiting, (await setup.PollAsync(started.AttemptId))!.State);
        Assert.Equal(1, issuer.TokenCalls);

        issuer.TokenError = null;
        _time.Advance(TimeSpan.FromSeconds(5));
        var approved = await setup.PollAsync(started.AttemptId);
        Assert.Equal(HostedSetupState.Approved, approved!.State);
        Assert.Equal("maya@example.com", approved.Email);

        var approval = setup.GetApproval(started.AttemptId)!;
        Assert.Equal(issuer.InstallationId, approval.InstallationId);
        using var key = InstallationKey.Import(approval.PrivateKey);
        Assert.Equal(issuer.DeviceForm!["installation_key"], key.PublicKey);

        // Approved state is stable and does not call the issuer again.
        await setup.PollAsync(started.AttemptId);
        Assert.Equal(2, issuer.TokenCalls);
    }

    [Fact]
    public async Task Slow_down_lengthens_the_interval()
    {
        var issuer = new FakeIssuer { TokenError = "slow_down" };
        using var setup = Coordinator(issuer);
        var started = await setup.StartAsync("Studio Mac");
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(10), (await setup.PollAsync(started.AttemptId))!.PollInterval);
    }

    [Theory]
    [InlineData("access_denied", HostedSetupState.Denied)]
    [InlineData("expired_token", HostedSetupState.Expired)]
    [InlineData("invalid_grant", HostedSetupState.Failed)]
    public async Task Terminal_answers_end_the_attempt(string error, HostedSetupState expected)
    {
        var issuer = new FakeIssuer { TokenError = error };
        using var setup = Coordinator(issuer);
        var started = await setup.StartAsync("Studio Mac");
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, (await setup.PollAsync(started.AttemptId))!.State);
        Assert.Null(setup.GetApproval(started.AttemptId));
    }

    [Fact]
    public async Task Attempt_expires_on_its_own_clock()
    {
        var issuer = new FakeIssuer { TokenError = "authorization_pending" };
        using var setup = Coordinator(issuer);
        var started = await setup.StartAsync("Studio Mac");
        _time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HostedSetupState.Expired, (await setup.PollAsync(started.AttemptId))!.State);
    }

    [Fact]
    public async Task Rejected_registration_fails_the_attempt()
    {
        var issuer = new FakeIssuer { RegistrationStatus = HttpStatusCode.Conflict };
        using var setup = Coordinator(issuer);
        var started = await setup.StartAsync("Studio Mac");
        _time.Advance(TimeSpan.FromSeconds(5));
        var result = await setup.PollAsync(started.AttemptId);
        Assert.Equal(HostedSetupState.Failed, result!.State);
        Assert.Null(setup.GetApproval(started.AttemptId));
    }

    [Fact]
    public async Task Unknown_or_removed_attempt_is_not_found()
    {
        using var setup = Coordinator(new FakeIssuer());
        Assert.Null(await setup.PollAsync("nope"));
        var started = await setup.StartAsync("Studio Mac");
        setup.Remove(started.AttemptId);
        Assert.Null(await setup.PollAsync(started.AttemptId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public async Task Installation_name_is_required_and_plain(string name)
    {
        using var setup = Coordinator(new FakeIssuer());
        var error = await Assert.ThrowsAsync<HostedAccountException>(() => setup.StartAsync(name));
        Assert.Equal("invalid_installation_name", error.Code);
    }

    private HostedSetupCoordinator Coordinator(FakeIssuer issuer)
    {
        issuer.Now = _time.GetUtcNow;
        return new(new HostedAccountClient(new HttpClient(issuer), Options.Create(new HostedAccountOptions { Issuer = HostedAccountClientTests.Issuer }), _time), _time);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
