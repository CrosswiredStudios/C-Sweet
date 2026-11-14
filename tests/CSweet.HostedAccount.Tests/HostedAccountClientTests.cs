using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CSweet.HostedAccount.Tests;

public sealed class HostedAccountClientTests
{
    internal const string Issuer = "https://account.test/";

    [Fact]
    public async Task Start_sends_the_installation_name_and_key_and_keeps_every_link_on_the_issuer()
    {
        var issuer = new FakeIssuer();
        using var key = InstallationKey.Create();
        var device = await Client(issuer).StartAsync("Northstar Headquarters", key);

        Assert.Equal("Northstar Headquarters", issuer.DeviceForm!["installation_name"]);
        Assert.Equal(key.PublicKey, issuer.DeviceForm["installation_key"]);
        Assert.Equal("csweet-hq", issuer.DeviceForm["client_id"]);
        Assert.Equal("WDJB-MJHT", device.UserCode);
        Assert.Equal("https://account.test/device?user_code=WDJB-MJHT", device.VerificationUriComplete.AbsoluteUri);
        Assert.Equal("account.test", device.CreateAccountUri.Host);
        Assert.Contains(Uri.EscapeDataString("/device?user_code=WDJB-MJHT"), device.CreateAccountUri.AbsoluteUri);
    }

    [Fact]
    public async Task Start_refuses_a_verification_link_on_another_host()
    {
        var issuer = new FakeIssuer { VerificationUri = "https://evil.example/device" };
        using var key = InstallationKey.Create();
        var error = await Assert.ThrowsAsync<HostedAccountException>(() => Client(issuer).StartAsync("Northstar", key));
        Assert.Equal("issuer_mismatch", error.Code);
    }

    [Fact]
    public async Task Discovery_for_another_issuer_is_rejected()
    {
        var issuer = new FakeIssuer { AdvertisedIssuer = "https://other.test/" };
        using var key = InstallationKey.Create();
        var error = await Assert.ThrowsAsync<HostedAccountException>(() => Client(issuer).StartAsync("Northstar", key));
        Assert.Equal("issuer_mismatch", error.Code);
    }

    [Theory]
    [InlineData("authorization_pending", DevicePollStatus.Pending)]
    [InlineData("slow_down", DevicePollStatus.SlowDown)]
    [InlineData("access_denied", DevicePollStatus.Denied)]
    [InlineData("expired_token", DevicePollStatus.Expired)]
    [InlineData("invalid_grant", DevicePollStatus.Failed)]
    public async Task Poll_maps_protocol_errors(string error, DevicePollStatus expected)
    {
        var issuer = new FakeIssuer { TokenError = error };
        Assert.Equal(expected, (await Client(issuer).PollAsync("device-code")).Status);
    }

    [Fact]
    public async Task Approved_poll_returns_the_verified_account()
    {
        var issuer = new FakeIssuer();
        var result = await Client(issuer).PollAsync("device-code");
        Assert.Equal(DevicePollStatus.Approved, result.Status);
        Assert.Equal(new HostedIdentity(Issuer, "market-user-1", "maya@example.com"), result.Identity);
        Assert.Equal("access-token", result.AccessToken);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("unverified")]
    [InlineData("signature")]
    public async Task Id_token_that_cannot_be_trusted_is_rejected(string defect)
    {
        var issuer = new FakeIssuer { IdTokenDefect = defect };
        var error = await Assert.ThrowsAsync<HostedAccountException>(() => Client(issuer).PollAsync("device-code"));
        Assert.Equal("invalid_id_token", error.Code);
    }

    [Fact]
    public async Task Registration_sends_a_proof_the_market_can_verify()
    {
        var issuer = new FakeIssuer();
        using var key = InstallationKey.Create();
        var registration = await Client(issuer).RegisterInstallationAsync("access-token", key);

        Assert.Equal(issuer.InstallationId, registration.InstallationId);
        Assert.Equal("Bearer access-token", issuer.RegistrationAuthorization);
        var body = issuer.RegistrationBody!.Value;
        Assert.Equal(key.PublicKey, body.GetProperty("publicKey").GetString());

        // Same check the Market performs (InstallationKeys.Verify) with the same message format.
        var spki = Decode(body.GetProperty("publicKey").GetString()!);
        var timestamp = body.GetProperty("timestamp").GetInt64();
        var message = $"csweet-installation-register\n{key.Thumbprint}\n{timestamp}\n{Encode(SHA256.HashData(Encoding.ASCII.GetBytes("access-token")))}";
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(spki, out _);
        Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(message), Decode(body.GetProperty("signature").GetString()!),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(Encode(SHA256.HashData(spki)), key.Thumbprint);
    }

    [Fact]
    public async Task Conflicting_registration_is_reported()
    {
        var issuer = new FakeIssuer { RegistrationStatus = HttpStatusCode.Conflict };
        using var key = InstallationKey.Create();
        var error = await Assert.ThrowsAsync<HostedAccountException>(() => Client(issuer).RegisterInstallationAsync("access-token", key));
        Assert.Equal("installation_conflict", error.Code);
    }

    [Fact]
    public void Http_issuer_is_refused_unless_explicitly_allowed_for_development()
    {
        Assert.Throws<InvalidOperationException>(() => new HostedAccountOptions { Issuer = "http://localhost:5000" }.IssuerUri);
        Assert.Equal("http://localhost:5000/", new HostedAccountOptions { Issuer = "http://localhost:5000", AllowHttpIssuer = true }.IssuerUri.AbsoluteUri);
    }

    [Fact]
    public void Private_key_round_trips_for_protected_storage()
    {
        using var key = InstallationKey.Create();
        using var restored = InstallationKey.Import(key.ExportPrivateKey());
        Assert.Equal(key.PublicKey, restored.PublicKey);
    }

    private static HostedAccountClient Client(FakeIssuer issuer) =>
        new(new HttpClient(issuer), Options.Create(new HostedAccountOptions { Issuer = Issuer }), TimeProvider.System);

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
