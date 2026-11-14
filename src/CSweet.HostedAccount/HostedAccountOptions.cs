namespace CSweet.HostedAccount;

/// <summary>
/// Hosted setup: Headquarters signs in its first administrator with a C-Sweet account and registers itself with
/// the account issuer. Configuration section <c>Installation</c>.
/// </summary>
public sealed class HostedAccountOptions
{
    public const string SectionName = "Installation";

    /// <summary>Shows the Hosted choice at first run. Off in production until hosted onboarding is accepted.</summary>
    public bool LinkingEnabled { get; set; }

    /// <summary>The C-Sweet account issuer. Every linked installation trusts only this address.</summary>
    public string Issuer { get; set; } = "https://account.c-sweet.com/";

    public string ClientId { get; set; } = "csweet-hq";

    /// <summary>Local development only: allow an http issuer such as the Market Aspire AppHost.</summary>
    public bool AllowHttpIssuer { get; set; }

    public Uri IssuerUri
    {
        get
        {
            var value = Issuer.EndsWith('/') ? Issuer : Issuer + "/";
            var uri = new Uri(value, UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps && !(AllowHttpIssuer && uri.Scheme == Uri.UriSchemeHttp))
                throw new InvalidOperationException("Installation:Issuer must use HTTPS.");
            return uri;
        }
    }
}
