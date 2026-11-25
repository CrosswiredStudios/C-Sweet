using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Setup;

internal static class OfficeLinkEndpoints
{
    internal static void MapOfficeLinks(this WebApplication app)
    {
        app.MapGet("/api/setup/office-links/{id:length(64)}", async (string id, HttpContext http, CSweetDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) return Results.NotFound();
            var gateway = Gateway(config);
            if (gateway is null) return Results.StatusCode(503);
            var now = DateTimeOffset.UtcNow;
            var entry = await db.ExecutionNodeEnrollments.AsNoTracking().Where(e => e.OfficeLinkId == id && e.ExpiresAt > now &&
                e.Status == ExecutionEnrollmentStatus.Available).Select(e => new { e.Id, e.SealedEnrollmentToken }).SingleOrDefaultAsync(ct);
            return entry?.SealedEnrollmentToken is { } sealedToken
                ? Results.Ok(new OfficeLinkGrant(gateway.AbsoluteUri, config["CSweet:ExecutionGateway:PublicCertificateSha256"], sealedToken, entry.Id))
                : Results.NotFound();
        }).AllowAnonymous();

        app.MapGet("/api/setup/office-links/confirm", (HttpContext http, IAntiforgery csrf) =>
        {
            var q = http.Request.Query;
            var link = Read(q["machine"], q["key"], q["until"]);
            if (link == null) return Page(http, "Request expired", "Open Office on your computer to start again.");
            if (http.User.Identity?.IsAuthenticated != true)
            {
                var resume = Uri.EscapeDataString("/api/setup/office-links/confirm?" + link.Query);
                return Results.LocalRedirect((http.RequestServices.GetService<HostedWebTenant>() == null
                    ? "/login?returnUrl=" : "/api/auth/account/start?officeReturn=") + resume);
            }
            var antiforgery = csrf.GetAndStoreTokens(http);
            var e = HtmlEncoder.Default;
            var form = $"""
                <form method="post"><input type="hidden" name="{antiforgery.FormFieldName}" value="{e.Encode(antiforgery.RequestToken!)}">
                <input type="hidden" name="machine" value="{e.Encode(link.Machine)}"><input type="hidden" name="key" value="{e.Encode(link.Key)}">
                <input type="hidden" name="until" value="{link.Until}"><p>Match this code on the computer you are adding.</p>
                <strong class="code">{link.MatchingCode}</strong><p><button>Link this Office</button></p></form>
                """;
            return Page(http, "Connect " + link.Machine, "Link this computer to Headquarters at " + http.Request.Host + ".", form);
        }).AllowAnonymous();

        app.MapPost("/api/setup/office-links/confirm", async (HttpContext http, CSweetDbContext db,
            IExecutionFleetService fleet, IConfiguration config, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var link = Read(form["machine"], form["key"], form["until"]);
            if (link == null) return Page(http, "Request expired", "Start a new request on the Office computer.");
            IResult result;
            try { result = await ApproveAsync(link, http, db, fleet, config, ct); }
            catch (Exception ex) when (ex.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "23505" })
            {
                return Page(http, "Another request just finished", "Open the approval link again to check this device and the available slots.", status: 409);
            }
            if (result is IStatusCodeHttpResult { StatusCode: 204 })
                return Page(http, "Return to your Office", "Finish installation on your computer. Headquarters will verify its identity and check whether it can run work.");
            return result is IStatusCodeHttpResult status ? status.StatusCode switch
            {
                403 => Page(http, "Administrator access required", "Sign in with the account that manages this Headquarters.", status: 403),
                409 => Page(http, "All device slots are in use", "Open Offices in Headquarters to review connected devices and pending reservations, or add a linked-device slot.", status: 409),
                503 => Page(http, "Office linking is unavailable", "Ask your administrator to verify the Office gateway and the hosted plan, then open your approval link again.", status: 503),
                _ => result
            } : result;
        }).RequireAuthorization("HostAdministration");
    }

    private static OfficeLinkRequest? Read(string? name, string? key, string? until)
    {
        if (!long.TryParse(until, out var expires)) return null;
        var link = new OfficeLinkRequest(name ?? "", key ?? "", expires);
        return link.IsValid(DateTimeOffset.UtcNow) ? link : null;
    }

    private static IResult Page(HttpContext http, string title, string summary, string form = "", int status = 200)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        var e = HtmlEncoder.Default;
        return Results.Content($$"""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Link Office · C-Sweet</title><style>
            :root{color-scheme:dark}body{margin:0;background:#101721;color:#edf3fb;font:1rem/1.6 system-ui}main{max-width:30rem;margin:12vh auto;padding:2rem;border:1px solid #334354;border-radius:18px;background:#182431}
            h1{line-height:1.2;overflow-wrap:anywhere}.code{font:600 2rem monospace;letter-spacing:.12em;color:#9ce6d8}button{padding:.8rem 1.2rem;border:0;border-radius:8px;background:#95e3d1;color:#102621;font:bold 1rem system-ui;cursor:pointer}
            </style><main><small>C-Sweet Headquarters</small><h1>{{e.Encode(title)}}</h1><p>{{e.Encode(summary)}}</p>{{form}}</main></html>
            """, "text/html", statusCode: status);
    }

    private static async Task<IResult> ApproveAsync(OfficeLinkRequest link, HttpContext http, CSweetDbContext db,
        IExecutionFleetService fleet, IConfiguration config, CancellationToken ct)
    {
            http.Response.Headers.CacheControl = "no-store";
            if (!link.IsValid(DateTimeOffset.UtcNow)) return Results.BadRequest();
            if (Gateway(config) is not { } gateway || (http.RequestServices.GetService<HostedWebTenant>() != null && gateway.IsLoopback))
                return Results.Problem("The administrator must configure a reachable HTTPS Office gateway.", statusCode: 503);
            if (http.RequestServices.GetService<HostedWebTenant>() is { } tenant)
                return await HostedOfficeEndpoints.EnrollDeviceAsync(http, fleet, tenant, ct, link);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            if (await db.ExecutionNodeEnrollments.AnyAsync(e => e.OfficeLinkId == link.Id, ct)) return Results.NoContent();
            await SealAsync(db, link, await CreateEnrollmentAsync(http, db, fleet, ct), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
    }

    private static Uri? Gateway(IConfiguration config) =>
        Uri.TryCreate(config["CSweet:ExecutionGateway:PublicUrl"], UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;

    internal static async Task<ExecutionCapacityActionResponse> CreateEnrollmentAsync(HttpContext http, CSweetDbContext db,
        IExecutionFleetService fleet, CancellationToken ct)
    {
        // A newly named Headquarters can receive a phone link before anyone opens its setup wizard.
        if (!await db.AgentRuntimeGlobalSettings.AnyAsync(ct) || !await db.SystemConfigurations.AnyAsync(ct))
            await http.RequestServices.GetRequiredService<ISetupService>().EnsureSeededAsync(ct);
        return await fleet.CreateEnrollmentAsync(ct);
    }

    internal static async Task SealAsync(CSweetDbContext db, OfficeLinkRequest link, ExecutionCapacityActionResponse result, CancellationToken ct)
    {
        if (result.Enrollment?.EnrollmentToken is not { } token) throw new InvalidOperationException("Enrollment was unavailable.");
        var row = await db.ExecutionNodeEnrollments.SingleAsync(e => e.Id == result.Enrollment.Id, ct);
        row.OfficeLinkId = link.Id;
        row.SealedEnrollmentToken = link.SealToken(token);
        row.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(link.Until);
        await db.SaveChangesAsync(ct);
    }
}
