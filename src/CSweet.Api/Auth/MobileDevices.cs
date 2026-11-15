using System.Net;
using System.Security.Claims;
using System.Text;
using CSweet.Contracts.Mobile;
using CSweet.Domain.Setup;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using CSweet.Mobile.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Auth;

public sealed record MobileEnrollmentResult(string Enrollment, MobileDevice? Device);

/// <summary>
/// Phone enrollment and approval (H4). A phone proves its key in TLS and presents the C-Sweet directory's assertion
/// that an account holds that key; the account must be linked to a local person. New phones wait for that person to
/// approve them from a signed-in session.
/// </summary>
public sealed class MobileDeviceService(
    CSweetDbContext db,
    UserManager<ApplicationUser> users,
    HostedAccountClient hostedAccount,
    TimeProvider time)
{
    private const int MaxLabelLength = 80;
    private static readonly TimeSpan AssertionSkew = TimeSpan.FromMinutes(1);

    public async Task<MobileEnrollmentResult> EnrollAsync(string? assertion, string phonePin, string? label, CancellationToken ct)
    {
        var link = await db.InstallationLinks.AsNoTracking().Where(x => x.State == InstallationLinkState.Active)
            .OrderByDescending(x => x.LinkedAt).FirstOrDefaultAsync(ct);
        if (link is null) return new(HeadquartersHello.EnrollmentRejected, null);

        var claim = RelayProtocol.ReadDeviceAssertion(assertion, await hostedAccount.GetDirectoryTicketKeyAsync(ct));
        var now = time.GetUtcNow();
        if (claim is null || claim.InstallationId != link.InstallationId || claim.PhoneKeyThumbprint != phonePin || claim.ExpiresAt + AssertionSkew < now)
            return new(HeadquartersHello.EnrollmentRejected, null);

        var user = await users.FindByLoginAsync(link.Issuer, claim.Subject);
        if (user is null || await users.IsLockedOutAsync(user)) return new(HeadquartersHello.EnrollmentNoAccess, null);

        var device = await db.MobileDevices.SingleOrDefaultAsync(x => x.KeyPin == phonePin, ct);
        if (device is not null && device.ApplicationUserId != user.Id) return new(HeadquartersHello.EnrollmentRejected, null);
        if (device is null)
        {
            device = new MobileDevice
            {
                Id = Guid.NewGuid(),
                ApplicationUserId = user.Id,
                KeyPin = phonePin,
                Label = CleanLabel(label),
                State = MobileDeviceState.Pending,
                RequestedAt = now
            };
            db.MobileDevices.Add(device);
        }
        device.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return new(StateName(device, user), device);
    }

    /// <summary>The person behind an active phone, rechecked on every request: phone active, person present and not locked out, security stamp unchanged.</summary>
    public async Task<(MobileDevice Device, ApplicationUser User)?> ResolveActiveAsync(string phonePin, CancellationToken ct)
    {
        var device = await db.MobileDevices.SingleOrDefaultAsync(x => x.KeyPin == phonePin && x.State == MobileDeviceState.Active, ct);
        if (device is null) return null;
        var user = await users.FindByIdAsync(device.ApplicationUserId.ToString());
        if (user is null || await users.IsLockedOutAsync(user) || device.SecurityStampAtApproval != await users.GetSecurityStampAsync(user)) return null;
        return (device, user);
    }

    public async Task<IReadOnlyList<MobileDeviceResponse>> ListMineAsync(Guid userId, CancellationToken ct) =>
        (await db.MobileDevices.AsNoTracking().Where(x => x.ApplicationUserId == userId).OrderByDescending(x => x.RequestedAt).ToListAsync(ct))
            .Select(Response).ToList();

    /// <summary>Approve, deny or revoke one of the signed-in person's own phones. Returns null when it is not theirs or the change is not allowed.</summary>
    public async Task<MobileDeviceResponse?> DecideAsync(Guid userId, Guid deviceId, string decision, CancellationToken ct)
    {
        var device = await db.MobileDevices.SingleOrDefaultAsync(x => x.Id == deviceId && x.ApplicationUserId == userId, ct);
        if (device is null) return null;
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return null;
        switch (decision, device.State)
        {
            case ("approve", MobileDeviceState.Pending):
                device.State = MobileDeviceState.Active;
                device.SecurityStampAtApproval = await users.GetSecurityStampAsync(user);
                break;
            case ("deny", MobileDeviceState.Pending):
                device.State = MobileDeviceState.Denied;
                break;
            case ("revoke", MobileDeviceState.Active):
                device.State = MobileDeviceState.Revoked;
                break;
            default:
                return null;
        }
        device.DecidedAt = time.GetUtcNow();
        device.DecidedByUserId = userId;
        device.Revision++;
        await db.SaveChangesAsync(ct);
        return Response(device);
    }

    private static string StateName(MobileDevice device, ApplicationUser user) => device.State switch
    {
        MobileDeviceState.Active => HeadquartersHello.EnrollmentActive,
        MobileDeviceState.Denied => HeadquartersHello.EnrollmentDenied,
        MobileDeviceState.Revoked => HeadquartersHello.EnrollmentRevoked,
        _ => HeadquartersHello.EnrollmentPending
    };

    private static MobileDeviceResponse Response(MobileDevice device) =>
        new(device.Id, device.Label, RelayProtocol.Fingerprint(device.KeyPin), device.State.ToString(), device.RequestedAt, device.DecidedAt, device.LastSeenAt);

    private static string CleanLabel(string? label)
    {
        var clean = new string((label ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Phone";
        return clean.Length > MaxLabelLength ? clean[..MaxLabelLength] : clean;
    }
}

public static class MobileDeviceEndpoints
{
    /// <summary>Settings → Phones for the signed-in person (cookie session with antiforgery). Never dispatched from a phone.</summary>
    public static IEndpointRouteBuilder MapMobileDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mobile/devices");
        group.MapGet("", async (ClaimsPrincipal user, MobileDeviceService devices, CancellationToken ct) =>
            UserId(user) is { } id ? Results.Ok(await devices.ListMineAsync(id, ct)) : Results.Unauthorized());
        foreach (var decision in new[] { "approve", "deny", "revoke" })
        {
            group.MapPost($"/{{deviceId:guid}}/{decision}", async (Guid deviceId, ClaimsPrincipal user, MobileDeviceService devices, CancellationToken ct) =>
            {
                if (UserId(user) is not { } id) return Results.Unauthorized();
                return await devices.DecideAsync(id, deviceId, decision, ct) is { } updated ? Results.Ok(updated) : Results.Conflict();
            });
        }
        return endpoints;
    }

    private static Guid? UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(MobileRequestDispatcher.DeviceClaim) is null && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

/// <summary>Marks a request that entered through the mobile dispatcher. Features cannot be set by HTTP clients.</summary>
public sealed class MobileDispatchFeature(Guid deviceId)
{
    public Guid DeviceId { get; } = deviceId;
}

/// <summary>
/// Runs a phone's request through the real API pipeline in process (TS-02 mechanism). The identity is rebuilt from the
/// device record for every request; only allowlisted read routes are dispatched; only status, content type and body
/// return. The pipeline is captured by the first middleware, so it includes routing, authorization and every
/// middleware the browser path uses.
/// </summary>
public sealed class MobileRequestDispatcher(IServiceScopeFactory scopes, IHttpContextFactory contexts,
    Microsoft.Extensions.Options.IOptions<MobileShellOptions> shellOptions)
{
    public const string DeviceClaim = "csweet:mobile_device";
    public const string AuthenticationType = "CSweet.MobileDevice";

    /// <summary>Routes a phone may call. Reads only until operation keys exist (TS-05); replaced by route markers (TS-04).</summary>
    private static readonly HashSet<string> AllowedReads = new(StringComparer.OrdinalIgnoreCase) { "/api/core/organizations" };

    public RequestDelegate? Pipeline { get; set; }

    private sealed class RequestBodyDetection(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    /// <summary>Only these request headers exist on a dispatched request; cookies, Authorization, Origin never cross.</summary>
    private static HeaderDictionary RequestHeaders(MobileRequest request, int bodyLength)
    {
        var headers = new HeaderDictionary { ["Accept"] = "application/json", ["Host"] = "mobile.headquarters.internal" };
        if (request.Body is not null)
        {
            headers["Content-Type"] = request.ContentType;
            headers.ContentLength = bodyLength;
        }
        if (request.OperationKey is { } key) headers[MobileDispatchGateMiddleware.OperationKeyHeader] = key;
        return headers;
    }

    /// <summary>API responses are capped at 4 MiB in the prototype (the register's 256 KiB is for request bodies).</summary>
    public const int MaxApiResponseBytes = 4 * 1024 * 1024;

    /// <summary>Whether a phone may send this request at all: GET only, and an allowlisted path (or any /api read in the prototype).</summary>
    public bool IsAllowed(MobileRequest request)
    {
        var path = request.Path.Split('?', 2)[0];
        // Never approve or list devices, or change the C-Sweet link, from a phone.
        if (path.StartsWith("/api/mobile/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/installation/", StringComparison.OrdinalIgnoreCase)) return false;
        if (WriteMethods.Contains(request.Method))
        {
            // Writes: JSON body within the cap and an operation key; the route marker and key binding are checked
            // after routing by MobileDispatchGateMiddleware.
            return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.Contains("..", StringComparison.Ordinal)
                && request.OperationKey is { Length: > 0 and <= MobileRequest.MaxOperationKeyLength }
                && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true
                && request.Body is { } body && body.Length <= (MobileRequest.MaxBodyBytes + 2) / 3 * 4;
        }
        if (!string.Equals(request.Method, "GET", StringComparison.Ordinal)) return false;
        return shellOptions.Value.AllowAllReads
            ? path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.Contains("..", StringComparison.Ordinal)
            : !request.Path.Contains('?') && AllowedReads.Contains(path);
    }

    private static readonly HashSet<string> WriteMethods = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task<TunnelReply> DispatchAsync(MobileRequest request, string phonePin, CancellationToken ct)
    {
        if (!IsAllowed(request)) return TunnelReply.Empty(StatusCodes.Status404NotFound);
        byte[] requestBody;
        try { requestBody = request.Body is null ? [] : Convert.FromBase64String(request.Body); }
        catch (FormatException) { return TunnelReply.Empty(StatusCodes.Status400BadRequest); }
        if (requestBody.Length > MobileRequest.MaxBodyBytes) return TunnelReply.Empty(StatusCodes.Status413PayloadTooLarge);
        if (Pipeline is null) return TunnelReply.Empty(StatusCodes.Status503ServiceUnavailable);

        await using var scope = scopes.CreateAsyncScope();
        var devices = scope.ServiceProvider.GetRequiredService<MobileDeviceService>();
        if (await devices.ResolveActiveAsync(phonePin, ct) is not { } active)
            return TunnelReply.Empty(StatusCodes.Status401Unauthorized);

        var principalFactory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var basePrincipal = await principalFactory.CreateAsync(active.User);
        var identity = new ClaimsIdentity(basePrincipal.Claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(DeviceClaim, active.Device.Id.ToString()));

        var body = new MemoryStream();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = request.Method,
            Scheme = "https",
            Protocol = "HTTP/1.1",
            PathBase = string.Empty,
            Path = request.Path.Split('?', 2)[0],
            QueryString = request.Path.Contains('?') ? "?" + request.Path.Split('?', 2)[1] : string.Empty,
            Headers = RequestHeaders(request, requestBody.Length),
            Body = requestBody.Length == 0 ? Stream.Null : new MemoryStream(requestBody, writable: false)
        });
        // Without this, minimal APIs treat the request as bodiless and never bind the JSON body (every phone write failed).
        features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetection(requestBody.Length > 0));
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = aborted.Token });
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { ConnectionId = "mobile-" + active.Device.Id.ToString("N") });
        features.Set(new MobileDispatchFeature(active.Device.Id));

        var context = contexts.Create(features);
        try
        {
            context.User = new ClaimsPrincipal(identity);
            await Pipeline(context);
            await context.Response.CompleteAsync();
            if (body.Length > MaxApiResponseBytes) return TunnelReply.Empty(StatusCodes.Status502BadGateway);
            // Only status, content type and body cross; Set-Cookie, Location and every other header stay here.
            return new TunnelReply(context.Response.StatusCode, context.Response.ContentType, null, body.ToArray());
        }
        finally
        {
            contexts.Dispose(context);
        }
    }
}
