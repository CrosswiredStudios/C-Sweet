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
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = e.Message }); }
        });
        group.MapGet("/", async (Guid installationId, HttpContext http, [FromServices] PluginAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.GetAsync(http.User.GetApplicationUserId()!.Value, installationId, ct)));
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
