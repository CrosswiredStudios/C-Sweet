using System.Net;
using System.Net.Http.Json;
using CSweet.Api.Agents;
using CSweet.Api.Auth;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ConnectionPhoneSetupTests
{
    [Theory]
    [InlineData("SelfHosted", true, true, "Configured")]
    [InlineData("SelfHosted", false, true, "SetupRequired")]
    [InlineData("SelfHosted", true, false, "LinkRequired")]
    [InlineData("Official", false, true, "HostedRouteRequired")]
    [InlineData("Skip", false, true, "Skipped")]
    public async Task GuideUsesCurrentLinkAndOnlyExportsPublicRouteValues(string mode, bool configured, bool sameIssuer, string state)
    {
        const string origin = "https://connections.example.test";
        using var hqKey = InstallationKey.Create();
        using var directoryKey = InstallationKey.Create();
        var id = Guid.NewGuid();
        var account = Options.Create(new HostedAccountOptions { LinkingEnabled = true });
        var protection = new EphemeralDataProtectionProvider();
        var dbOptions = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(dbOptions);
        db.InstallationLinks.Add(new InstallationLink {
            Id = Guid.NewGuid(), InstallationId = id, Issuer = sameIssuer ? account.Value.Issuer : "https://previous.example.test/",
            ProtectedInstallationKey = Convert.ToBase64String(protection.CreateProtector(AuthenticationService.InstallationKeyProtectionPurpose).Protect(hqKey.ExportPrivateKey()))
        });
        await db.SaveChangesAsync();
        var selections = new ConnectionServiceSelectionStore(db, Options.Create(new PluginConnectionOptions()));
        await selections.Save(Guid.NewGuid(), new(mode, mode == "SelfHosted" ? origin : null, 0), default);
        var relay = Options.Create(new HeadquartersRelayOptions { Enabled = configured, Origin = origin, RouteId = "existing-route" });
        using var handler = new TicketKeyResponse(directoryKey.PublicKey);
        using var http = new HttpClient(handler);
        var result = await ConnectionPhoneSetupEndpoint.Read(selections, relay, account,
            new EfDirectoryPublicationStore(db, protection, TimeProvider.System, account),
            new HostedAccountClient(http, account, TimeProvider.System), default);

        Assert.Equal(state, result.State);
        if (state is "Configured" or "SetupRequired")
        {
            Assert.Equal(directoryKey.PublicKey, result.DirectoryTicketKey);
            Assert.Equal(hqKey.PublicKey, result.HeadquartersKey);
            Assert.Equal(id, result.InstallationId);
            Assert.Equal(configured ? "existing-route" : id.ToString("N"), result.RouteId);
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            Assert.Null(result.HeadquartersKey);
            Assert.Null(result.DirectoryTicketKey);
            Assert.Equal(0, handler.Calls);
        }
    }

    private sealed class TicketKeyResponse(string ticketKey) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://account.c-sweet.com/api/directory/ticket-key", request.RequestUri!.AbsoluteUri);
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { key = ticketKey }) });
        }
    }
}
