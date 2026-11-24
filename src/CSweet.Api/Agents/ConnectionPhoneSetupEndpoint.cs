using System.Security.Cryptography;
using CSweet.Api.Auth;
using CSweet.Contracts.Plugins;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Agents;

internal static class ConnectionPhoneSetupEndpoint
{
    internal static async Task<ConnectionPhoneSetup> Read(ConnectionServiceSelectionStore selections,
        IOptions<HeadquartersRelayOptions> relayOptions, IOptions<HostedAccountOptions> account,
        IDirectoryPublicationStore directory, HostedAccountClient issuer, CancellationToken ct)
    {
        var choice = selections.Current();
        if (choice.Mode == "Skip") return new("Skipped");
        if (!account.Value.LinkingEnabled) return new("LinkingDisabled");
        var link = await directory.GetActiveLinkAsync(ct);
        if (link is null) return new("LinkRequired");
        var relay = relayOptions.Value;
        var origin = relay.Enabled ? relay.OriginUri?.GetLeftPart(UriPartial.Authority) : null;
        try
        {
            if (choice.Mode != "SelfHosted")
                return new(origin is not null && !string.IsNullOrWhiteSpace(relay.RouteId) ? "Configured" : "HostedRouteRequired", origin);

            var sameRoute = origin == choice.Origin && !string.IsNullOrWhiteSpace(relay.RouteId);
            var routeId = sameRoute ? relay.RouteId : link.InstallationId.ToString("N");
            using var key = InstallationKey.Import(link.PrivateKey);
            var ticketKey = await issuer.GetDirectoryTicketKeyAsync(ct);
            // Only public base64url values and a single route identifier enter the downloadable env file.
            if (!ticketKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                routeId.Length > 128 || !char.IsAsciiLetterOrDigit(routeId[0]) ||
                !routeId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
                return new("Unavailable");
            return new(sameRoute ? "Configured" : "SetupRequired", origin, routeId, link.InstallationId, key.PublicKey, ticketKey);
        }
        catch (HostedAccountException) { return new("Unavailable"); }
        finally { CryptographicOperations.ZeroMemory(link.PrivateKey); }
    }
}
