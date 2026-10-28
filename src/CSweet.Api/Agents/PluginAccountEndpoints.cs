using CSweet.Api.Auth;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Mvc;

namespace CSweet.Api.Agents;

public static class PluginAccountEndpoints
{
    public static IEndpointRouteBuilder MapPluginAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/plugins/installations/{installationId:guid}/account-management").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.User.GetApplicationUserId() is null) return Results.Forbid();
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (HttpRequestException) { return Results.Json(new { error = "The connection service is temporarily unavailable. Try again shortly." }, statusCode: 503); }
            catch(Microsoft.EntityFrameworkCore.DbUpdateException) { return Results.Conflict(new {error="Account access changed concurrently. Refresh and review again."}); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = e.Message }); }
        });
        group.MapGet("/", async (Guid installationId, HttpContext http, [FromServices] PluginAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.GetAsync(http.User.GetApplicationUserId()!.Value, installationId, ct)));
        group.MapPut("/operators",async(Guid installationId,SavePluginOperatorRequest request,HttpContext http,[FromServices] PluginAccountService accounts,CancellationToken ct)=>
        { await accounts.SaveOperatorAsync(http.User.GetApplicationUserId()!.Value,installationId,request,ct); return Results.NoContent(); });
        group.MapDelete("/operators/{operatorId:guid}",async(Guid installationId,Guid operatorId,long revision,HttpContext http,[FromServices] PluginAccountService accounts,CancellationToken ct)=>
        { await accounts.RevokeOperatorAsync(http.User.GetApplicationUserId()!.Value,installationId,operatorId,revision,ct); return Results.NoContent(); });
        group.MapGet("/impact",async(Guid installationId,HttpContext http,[FromServices] PluginAccountService accounts,CancellationToken ct)=>
            Results.Ok(await accounts.GetInstallationImpactAsync(http.User.GetApplicationUserId()!.Value,installationId,ct)));
        group.MapPost("/control",async(Guid installationId,PluginInstallationControlRequest request,HttpContext http,[FromServices] PluginAccountService accounts,CancellationToken ct)=>
        { await accounts.ControlInstallationAsync(http.User.GetApplicationUserId()!.Value,installationId,request,ct); return Results.NoContent(); });
        group.MapPost("/retry-build",async(Guid installationId,HttpContext http,[FromServices] PluginAccountService accounts,
            [FromServices] CSweet.Application.Setup.IAgentInstallationService installations,CancellationToken ct)=>
        { await accounts.RequireAdministrationAsync(http.User.GetApplicationUserId()!.Value,installationId,ct); return Results.Ok(await installations.RetryBuildAsync(installationId,ct)); });
        group.MapGet("/review",async(Guid installationId,HttpContext http,[FromServices] PluginAccountService accounts,
            [FromServices] CSweet.Infrastructure.Persistence.CSweetDbContext db,CancellationToken ct)=>
        {
            await accounts.RequireAdministrationAsync(http.User.GetApplicationUserId()!.Value,installationId,ct);
            var installation=await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.AgentInstallations,x=>x.PackageVersion),x=>x.Id==installationId,ct);
            return Results.Ok(new PluginPackageReview(installation.PackageVersion!.PackageDigest ?? "",installation.PackageVersion.ManifestJson,installation.BusinessId));
        });
        group.MapPost("/connect", async (Guid installationId, ConnectPluginAccountRequest request, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        {
            var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
            if (!http.Request.IsHttps && !(environment.IsDevelopment() &&
                (http.Request.Host.Host == "localhost" || System.Net.IPAddress.TryParse(http.Request.Host.Host, out var address) && System.Net.IPAddress.IsLoopback(address))))
                return Results.Problem("Secure account setup requires HTTPS.", statusCode: 400);
            var binding = CSweet.ConnectionService.Protocol.DeploymentProofCodec.Random();
            var response = await accounts.BeginAsync(http.User.GetApplicationUserId()!.Value, installationId, request, ct, binding);
            http.Response.Cookies.Append("CSweet.ManagedReturn." + response.SetupId.ToString("N"), binding,
                new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax,
                    Path = "/api/plugins", MaxAge = TimeSpan.FromMinutes(10), IsEssential = true });
            return Results.Ok(response);
        });
        endpoints.MapPost("/api/plugins/managed-connections/return", async (CompleteManagedPluginReturnRequest request,
            HttpContext http, [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        {
            if (http.User.GetApplicationUserId() is not { } user) return Results.Forbid();
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var result = await accounts.CompleteManagedAsync(user, request.AttemptId, request.Handoff,
                    http.Request.Cookies["CSweet.ManagedReturn." + request.AttemptId.ToString("N")], ct);
                return Results.Ok(result);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = "The managed connection return could not be completed. Return to the plugin and reconnect." }); }
        }).RequireAuthorization();
        var administration = endpoints.MapGroup("/api/plugins/managed-connections").RequireAuthorization("PluginAdministration");
        administration.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or ArgumentException or
                System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = "The managed service configuration or registration could not be verified." }); }
        });
        administration.MapPost("/pairing", async ([FromServices] ManagedPluginConnectionGateway gateway, CancellationToken ct) =>
            Results.Ok(await gateway.PreparePairing(ct)));
        administration.MapGet("/status", async ([FromServices] ManagedPluginConnectionGateway gateway, CancellationToken ct) =>
            Results.Ok(await gateway.Status(ct)));
        administration.MapPost("/pairing/complete", async (CompleteManagedConnectionPairing request,
            [FromServices] ManagedPluginConnectionGateway gateway, CancellationToken ct) =>
        { await gateway.CompletePairing(new(request.DeploymentId, request.TenantId, request.Revision), ct); return Results.NoContent(); });
        group.MapPost("/setup/{setupId:guid}/retry", async (Guid installationId, Guid setupId, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        { await accounts.RetryImportAsync(http.User.GetApplicationUserId()!.Value, installationId, setupId, ct); return Results.NoContent(); });
        group.MapPut("/{accountId:guid}/business-access", async (Guid installationId, Guid accountId, SavePluginAccountGrantRequest request, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        { await accounts.SaveGrantAsync(http.User.GetApplicationUserId()!.Value, installationId, accountId, request, ct); return Results.NoContent(); });
        group.MapDelete("/business-access/{grantId:guid}", async (Guid installationId, Guid grantId, long revision, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        { await accounts.RevokeGrantAsync(http.User.GetApplicationUserId()!.Value, installationId, grantId, revision, ct); return Results.NoContent(); });
        group.MapGet("/businesses/{organizationId:guid}", async (Guid installationId, Guid organizationId, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.GetBusinessAccountsAsync(http.User.GetApplicationUserId()!.Value, organizationId, installationId, ct)));
        group.MapPost("/businesses/{organizationId:guid}/bindings", async (Guid installationId, Guid organizationId, PluginAccountBindingRequest request, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        { await accounts.BindBusinessResourceAsync(http.User.GetApplicationUserId()!.Value, organizationId, installationId, request, ct); return Results.NoContent(); });
        group.MapGet("/{accountId:guid}/disconnect-impact", async (Guid installationId, Guid accountId, HttpContext http, [FromServices] PluginAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.GetDisconnectImpactAsync(http.User.GetApplicationUserId()!.Value, installationId, accountId, ct)));
        group.MapPost("/{accountId:guid}/disconnect", async (Guid installationId, Guid accountId, DisconnectPluginAccountRequest request, HttpContext http,
            [FromServices] PluginAccountService accounts, CancellationToken ct) =>
        { await accounts.DisconnectAsync(http.User.GetApplicationUserId()!.Value, installationId, accountId, request, ct); return Results.NoContent(); });
        return endpoints;
    }
}
