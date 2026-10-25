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
            Results.Ok(await accounts.BeginAsync(http.User.GetApplicationUserId()!.Value, installationId, request, ct)));
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
