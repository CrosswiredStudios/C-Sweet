using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CSweet.HostedAccount.Tests;

public sealed class DirectoryPublisherTests
{
    private readonly HostedSetupCoordinatorTests.ManualTime _time = new(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
    private readonly FakeIssuer _issuer;
    private readonly InstallationKey _key = InstallationKey.Create();
    private readonly MemoryStore _store;

    public DirectoryPublisherTests()
    {
        _issuer = new FakeIssuer { Now = () => _time.GetUtcNow() };
        _store = new MemoryStore(new LinkedInstallation(_issuer.InstallationId, HostedAccountClientTests.Issuer, _key.ExportPrivateKey()));
    }

    [Fact]
    public async Task Publishes_the_owner_signed_with_the_installation_key_once()
    {
        var publisher = Publisher();
        Assert.Equal(1, await publisher.RunOnceAsync(_store));
        Assert.Equal(("Owner", "Active", 1L), _issuer.Members["market-user-1"]);
        var entry = _store.Entries.Single();
        Assert.Equal(1, entry.Published);

        var body = _issuer.MemberBodies.Single();
        var message = $"csweet-installation-member\n{_issuer.InstallationId:D}\nmarket-user-1\nOwner\nActive\n1\n{body.GetProperty("timestamp").GetInt64()}";
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Decode(_key.PublicKey), out _);
        Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(message), Decode(body.GetProperty("signature").GetString()!),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // Nothing is due afterwards, and reconciliation does not add a second owner entry.
        Assert.Equal(0, await publisher.RunOnceAsync(_store));
        Assert.Single(_store.Entries);
        Assert.Single(_issuer.MemberBodies);
    }

    [Fact]
    public async Task A_newer_revision_held_by_CSweet_is_overtaken_so_headquarters_stays_the_source_of_truth()
    {
        _issuer.Members["market-user-1"] = ("Owner", "Removed", 5); // e.g. Headquarters restored from an old backup
        var publisher = Publisher();
        await publisher.RunOnceAsync(_store);
        Assert.Equal(6, _store.Entries.Single().Revision);
        await publisher.RunOnceAsync(_store);
        Assert.Equal(("Owner", "Active", 6L), _issuer.Members["market-user-1"]);
        Assert.Equal(6, _store.Entries.Single().Published);
    }

    [Fact]
    public async Task Outages_back_off_and_retry_until_CSweet_accepts()
    {
        _issuer.MemberStatusOverride = HttpStatusCode.ServiceUnavailable;
        var publisher = Publisher();
        await publisher.RunOnceAsync(_store);
        var entry = _store.Entries.Single();
        Assert.Equal(0, entry.Published);
        Assert.Equal("publish_failed", entry.LastError);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(30), entry.NextAttemptAt);

        _issuer.MemberStatusOverride = null;
        Assert.Equal(0, await publisher.RunOnceAsync(_store)); // not due yet
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await publisher.RunOnceAsync(_store));
        Assert.Equal(1, _store.Entries.Single().Published);
        Assert.Null(_store.Entries.Single().LastError);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "installation_retired")]
    [InlineData(HttpStatusCode.NotFound, "unknown_account")]
    public async Task Permanent_refusals_wait_hours_before_trying_again(HttpStatusCode status, string code)
    {
        _issuer.MemberStatusOverride = status;
        _issuer.MemberErrorCode = code;
        await Publisher().RunOnceAsync(_store);
        var entry = _store.Entries.Single();
        Assert.Equal(code, entry.LastError);
        Assert.Equal(_time.GetUtcNow() + DirectoryPublisher.PermanentFailureRetry, entry.NextAttemptAt);
    }

    [Fact]
    public async Task Private_installations_and_a_different_issuer_publish_nothing()
    {
        var unlinked = new MemoryStore(null);
        Assert.Equal(0, await Publisher().RunOnceAsync(unlinked));
        var elsewhere = new MemoryStore(new LinkedInstallation(_issuer.InstallationId, "https://other.test/", _key.ExportPrivateKey()));
        Assert.Equal(0, await Publisher().RunOnceAsync(elsewhere));
        Assert.Equal(0, await Publisher(linkingEnabled: false).RunOnceAsync(_store));
        Assert.Empty(_issuer.MemberBodies);
        Assert.Empty(elsewhere.Entries);
    }

    [Fact]
    public void Backoff_doubles_from_thirty_seconds_to_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DirectoryPublisher.Backoff(0));
        Assert.Equal(TimeSpan.FromSeconds(60), DirectoryPublisher.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(15), DirectoryPublisher.Backoff(9));
    }

    private static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private DirectoryPublisher Publisher(bool linkingEnabled = true)
    {
        var options = Options.Create(new HostedAccountOptions { LinkingEnabled = linkingEnabled, Issuer = HostedAccountClientTests.Issuer });
        var client = new HostedAccountClient(new HttpClient(_issuer), options, _time);
        return new DirectoryPublisher(client, options, _time);
    }

    private sealed class Entry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Subject { get; init; } = "";
        public long Revision { get; set; } = 1;
        public long Published { get; set; }
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
    }

    private sealed class MemoryStore(LinkedInstallation? link) : IDirectoryPublicationStore
    {
        public List<Entry> Entries { get; } = [];
        public Task<LinkedInstallation?> GetActiveLinkAsync(CancellationToken ct) => Task.FromResult(link);
        public Task EnsureOwnerPublicationAsync(LinkedInstallation l, CancellationToken ct)
        {
            if (Entries.Count == 0) Entries.Add(new Entry { Subject = "market-user-1" });
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PendingMemberPublication>> GetDueAsync(DateTimeOffset now, int max, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PendingMemberPublication>>(Entries
                .Where(x => x.Revision > x.Published && x.NextAttemptAt <= now).OrderBy(x => x.NextAttemptAt).Take(max)
                .Select(x => new PendingMemberPublication(x.Id, x.Subject, "Owner", "Active", x.Revision, x.Attempts)).ToList());
        public Task MarkPublishedAsync(Guid id, long revision, CancellationToken ct)
        {
            var e = Entries.Single(x => x.Id == id);
            e.Published = Math.Max(e.Published, revision); e.Attempts = 0; e.LastError = null;
            return Task.CompletedTask;
        }
        public Task RaiseRevisionAsync(Guid id, long aboveRevision, DateTimeOffset now, CancellationToken ct)
        {
            var e = Entries.Single(x => x.Id == id);
            e.Revision = Math.Max(e.Revision, aboveRevision + 1); e.NextAttemptAt = now;
            return Task.CompletedTask;
        }
        public Task MarkFailedAsync(Guid id, string error, DateTimeOffset nextAttemptAt, CancellationToken ct)
        {
            var e = Entries.Single(x => x.Id == id);
            e.Attempts++; e.LastError = error; e.NextAttemptAt = nextAttemptAt;
            return Task.CompletedTask;
        }
    }
}

public sealed class RelayPublicationTests
{
    [Fact]
    public async Task Relay_binding_is_signed_with_the_installation_key_over_the_exact_message()
    {
        var issuer = new FakeIssuer();
        var options = Options.Create(new HostedAccountOptions { LinkingEnabled = true, Issuer = HostedAccountClientTests.Issuer });
        var client = new HostedAccountClient(new HttpClient(issuer), options, TimeProvider.System);
        using var key = InstallationKey.Create();
        var installation = Guid.NewGuid();
        Assert.True(await client.PublishRelayAsync(installation, key, new Uri("https://connections.test"), "route-1", "PIN", 7));
        var body = issuer.RelayBody!.Value;
        Assert.Equal("https://connections.test/", body.GetProperty("relayOrigin").GetString());
        var message = $"csweet-installation-relay\n{installation:D}\nhttps://connections.test/\nroute-1\nPIN\n7\n{body.GetProperty("timestamp").GetInt64()}";
        using var verifier = ECDsa.Create();
        var spki = Decode(key.PublicKey);
        verifier.ImportSubjectPublicKeyInfo(spki, out _);
        Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes(message), Decode(body.GetProperty("signature").GetString()!),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
