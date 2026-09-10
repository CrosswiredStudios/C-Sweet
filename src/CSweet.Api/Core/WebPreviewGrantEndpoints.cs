using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Api.Core;

public static class WebPreviewGrantEndpoints
{
    public static IEndpointRouteBuilder MapWebPreviewGrantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/core/organizations/{organizationId:guid}/web-preview-grants/{grantId:guid}/revoke",
            async (Guid organizationId,Guid grantId,HttpContext http,WebPreviewGrantService service,IAuditEventWriter audit,CancellationToken token)=>
            {
                var userId=http.User.GetApplicationUserId();
                if(!userId.HasValue) return Results.Forbid();
                try
                {
                    var result=await service.RevokeAsync(organizationId,grantId,userId.Value,token);
                    await audit.WriteAsync("web-preview.grant.revoked","WebPreviewGrant",grantId,
                        "Standing private web preview access was revoked.",cancellationToken:token);
                    return Results.Ok(result);
                }
                catch(UnauthorizedAccessException) { return Results.Forbid(); }
                catch(Exception error) when(error is InvalidOperationException or DbUpdateConcurrencyException)
                { return Results.Conflict(new { message="The preview grant changed. Refresh the review and try again." }); }
            }).RequireAuthorization();
        return endpoints;
    }
}
