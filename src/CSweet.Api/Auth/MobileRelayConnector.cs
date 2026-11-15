using System.Net.Security;
using System.Security.Cryptography;
using CSweet.Domain.Setup;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using CSweet.Mobile.Transport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Auth;

/// <summary>Where this Headquarters meets phones (H4). Off by default.</summary>
public sealed class HeadquartersRelayOptions
{
    public const string Section = "MobileRelay";
    public bool Enabled { get; set; }
    /// <summary>The relay origin, for example https://connections.c-sweet.com/. http is accepted only for a loopback relay.</summary>
    public string? Origin { get; set; }
    /// <summary>The route the relay operator provisioned for this Headquarters.</summary>
    public string RouteId { get; set; } = string.Empty;

    public Uri? OriginUri =>
        Uri.TryCreate(Origin, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
}

/// <summary>
/// Keeps this linked Headquarters attached to the mobile relay. The installation key doubles as the relay route key;
/// a separate TLS key (stored protected with the installation link) is the identity phones pin. On start it publishes
/// the relay binding to C-Sweet so the directory can issue phone tickets. Until device enrollment exists, a phone
/// gets only a welcome that names this Headquarters and says enrollment is required — no business data.
/// </summary>
internal sealed class MobileRelayConnectorService(
    IServiceScopeFactory scopes,
    IOptions<HeadquartersRelayOptions> relayOptions,
    IOptions<HostedAccountOptions> hostedOptions,
    HostedAccountClient client,
    IDataProtectionProvider protection,
    MobileRequestDispatcher dispatcher,
    MobileWebAssets webAssets,
    InstallationLinkSignal linkSignal,
    ILogger<MobileRelayConnectorService> logger) : BackgroundService
{
    private const string TlsKeyProtectionPurpose = "CSweet.InstallationLink.TlsKey.v1";
    private static readonly TimeSpan WaitForLink = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = relayOptions.Value;
        if (!settings.Enabled || !hostedOptions.Value.LinkingEnabled) return;
        if (settings.OriginUri is not { } origin || string.IsNullOrWhiteSpace(settings.RouteId))
        {
            logger.LogError("MobileRelay is enabled but MobileRelay:Origin or MobileRelay:RouteId is missing or invalid.");
            return;
        }

        var failures = 0;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var linkChanged = linkSignal.Token; // taken before loading, so a change during the load is not missed
                    var identity = await LoadIdentityAsync(stoppingToken);
                    if (identity is null)
                    {
                        await Task.Delay(WaitForLink, stoppingToken);
                        continue;
                    }
                    using (identity)
                    {
                        logger.LogInformation(
                            "Mobile relay {Origin} route {RouteId}: installation {InstallationId}, route key {RouteKey}, TLS pin {Pin}.",
                            origin, settings.RouteId, identity.InstallationId, identity.RouteKeyPublic, identity.Pin);
                        using (var signer = InstallationKey.Import(identity.InstallationKeyBytes))
                            await client.PublishRelayAsync(identity.InstallationId, signer, origin, settings.RouteId, identity.Pin,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stoppingToken);

                        await using var connector = new HeadquartersRelayConnector(new SignalRRelayConnection(origin, reconnect: true),
                            settings.RouteId, identity.RouteKey, identity.Certificate, _ => true,
                            (tls, channel, ct) => SessionAsync(tls, channel, identity, ct));
                        await connector.StartAsync(stoppingToken);
                        failures = 0;
                        logger.LogInformation("Attached to the mobile relay as route {RouteId}.", settings.RouteId);
                        using var ended = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, linkChanged);
                        try
                        {
                            var reason = await connector.Ended.WaitAsync(ended.Token);
                            logger.LogWarning("Mobile relay connection ended ({Reason}); reconnecting.", reason);
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogInformation("The C-Sweet link changed; reattaching to the mobile relay with the new installation.");
                        }
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested && error is not OperationCanceledException)
                {
                    failures++;
                    logger.LogWarning(error, "Mobile relay connection failed; retrying.");
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(failures, 5)))), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One phone session: hello with the directory assertion → enrollment state; when the phone is active, its
    /// requests are dispatched one at a time, each re-authorized from the device record.
    /// </summary>
    private async Task SessionAsync(SslStream tls, PhoneChannel channel, RelayIdentity identity, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        PhoneHello hello;
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            first.CancelAfter(TimeSpan.FromSeconds(30));
            hello = await HeadquartersHello.ReadHelloAsync(tls, first.Token);
        }

        MobileEnrollmentResult enrollment;
        await using (var scope = scopes.CreateAsyncScope())
            enrollment = await scope.ServiceProvider.GetRequiredService<MobileDeviceService>().EnrollAsync(hello.Assertion, channel.PhonePin, hello.DeviceLabel, session.Token);
        await HeadquartersHello.SendWelcomeAsync(tls, new HeadquartersWelcome("welcome", HeadquartersHello.Protocol, identity.InstallationId,
            identity.DisplayName, channel.PhonePin, enrollment.Enrollment), session.Token);
        if (enrollment.Enrollment != HeadquartersHello.EnrollmentActive) return;

        // Enrolled: API reads go through the dispatcher (re-authorized each time); published web files come from the
        // manifest. The session ends when the phone closes it or after ten idle minutes.
        await HeadquartersTunnelServer.ServeAsync(tls, (request, token) =>
            request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ? dispatcher.DispatchAsync(request, channel.PhonePin, token)
            : request.Method != "GET" ? Task.FromResult(TunnelReply.Empty(StatusCodes.Status405MethodNotAllowed))
            : request.Path == MobileWebAssets.ManifestPath ? Task.FromResult(webAssets.Enabled ? webAssets.Manifest() : TunnelReply.Empty(StatusCodes.Status404NotFound))
            : webAssets.ReadAsync(request.Path, token),
            concurrency: 8, idle: TimeSpan.FromMinutes(10), ct);
    }

    /// <summary>The active link's installation key and TLS key; creates and stores the TLS key on first use.</summary>
    private async Task<RelayIdentity?> LoadIdentityAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var link = await db.InstallationLinks.Where(x => x.State == InstallationLinkState.Active).OrderByDescending(x => x.LinkedAt).FirstOrDefaultAsync(ct);
        if (link is null) return null;
        // Linked to a different C-Sweet service than the one configured now: wait for Settings → C-Sweet account to relink
        // rather than announcing that installation to the wrong issuer.
        if (!InstallationLinkEndpoints.SameIssuer(link.Issuer, hostedOptions.Value.Issuer))
        {
            logger.LogWarning("Linked to {LinkedIssuer} but configured for {Issuer}; link again in Settings → C-Sweet account.", link.Issuer, hostedOptions.Value.Issuer);
            return null;
        }

        var installationKey = protection.CreateProtector(AuthenticationService.InstallationKeyProtectionPurpose)
            .Unprotect(Convert.FromBase64String(link.ProtectedInstallationKey));
        var tlsProtector = protection.CreateProtector(TlsKeyProtectionPurpose);
        var tlsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (link.ProtectedTlsKey is { Length: > 0 } stored)
        {
            tlsKey.ImportPkcs8PrivateKey(tlsProtector.Unprotect(Convert.FromBase64String(stored)), out _);
        }
        else
        {
            link.ProtectedTlsKey = Convert.ToBase64String(tlsProtector.Protect(tlsKey.ExportPkcs8PrivateKey()));
            link.Revision++;
            await db.SaveChangesAsync(ct);
        }

        var routeKey = ECDsa.Create();
        routeKey.ImportPkcs8PrivateKey(installationKey, out _);
        return new RelayIdentity(link.InstallationId, link.DisplayName, installationKey, routeKey, tlsKey,
            PinnedTls.CreateCertificate(tlsKey, link.DisplayName, TimeSpan.FromDays(365)));
    }

    private sealed class RelayIdentity(Guid installationId, string displayName, byte[] installationKeyBytes, ECDsa routeKey, ECDsa tlsKey,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) : IDisposable
    {
        public Guid InstallationId { get; } = installationId;
        public string DisplayName { get; } = displayName;
        public byte[] InstallationKeyBytes { get; } = installationKeyBytes;
        public ECDsa RouteKey { get; } = routeKey;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; } = certificate;
        public string Pin { get; } = RelayProtocol.Thumbprint(tlsKey);
        public string RouteKeyPublic { get; } = RelayProtocol.PublicKey(routeKey);

        public void Dispose()
        {
            RouteKey.Dispose();
            tlsKey.Dispose();
            Certificate.Dispose();
            CryptographicOperations.ZeroMemory(InstallationKeyBytes);
        }
    }
}
