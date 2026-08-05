using System.Security.Claims;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.GenAi;

public static class GenAiEndpoints
{
    public static IEndpointRouteBuilder MapGenAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/genai-provider-profiles").RequireAuthorization("PluginAdministration");
        providers.MapGet("", async (IGenAiProviderProfileService service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(token)));
        providers.MapPost("/discover-local", async (ILocalGenAiProviderDiscoveryService service, CancellationToken token) =>
            Results.Ok(await service.DiscoverAsync(token)));
        providers.MapPost("/test", async (TestGenAiProviderConnectionRequest request,
            IGenAiProviderProfileService service, CancellationToken token) =>
            Results.Ok(await service.TestDraftAsync(request, token)));
        providers.MapGet("/{id:guid}", async (Guid id, IGenAiProviderProfileService service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } profile ? Results.Ok(profile) : Results.NotFound());
        providers.MapPost("", async (CreateGenAiProviderProfileRequest request, IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.CreateAsync(request, token);
            return result.Succeeded ? Results.Created($"/api/genai-provider-profiles/{result.Profile!.Id}", result) : Results.BadRequest(result);
        });
        providers.MapPut("/{id:guid}", async (Guid id, UpdateGenAiProviderProfileRequest request, IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.UpdateAsync(id, request, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        providers.MapDelete("/{id:guid}", async (Guid id, IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.DeleteAsync(id, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        providers.MapPost("/{id:guid}/test", async (Guid id, IGenAiProviderProfileService service, CancellationToken token) =>
            Results.Ok(await service.TestAsync(id, token)));
        providers.MapPost("/{providerId:guid}/operations", async (Guid providerId, SaveGenAiOperationConfigurationRequest request,
            IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.SaveOperationAsync(providerId, null, request, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        providers.MapPut("/{providerId:guid}/operations/{operationId:guid}", async (Guid providerId, Guid operationId,
            SaveGenAiOperationConfigurationRequest request, IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.SaveOperationAsync(providerId, operationId, request, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        providers.MapDelete("/{providerId:guid}/operations/{operationId:guid}", async (Guid providerId, Guid operationId,
            IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.DeleteOperationAsync(providerId, operationId, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });
        providers.MapPost("/defaults", async (SetGenAiOperationDefaultRequest request, IGenAiProviderProfileService service, CancellationToken token) =>
        {
            var result = await service.SetDefaultAsync(request.OperationConfigurationId, token);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        var jobs = endpoints.MapGroup("/api/genai-jobs");
        jobs.MapGet("/{id:guid}", async (Guid id, Guid organizationId, ClaimsPrincipal user, CSweetDbContext db,
            IGenAiJobService service, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            return await service.GetAsync(id, organizationId, cancellationToken: token) is { } job ? Results.Ok(job) : Results.NotFound();
        });
        jobs.MapPost("/{id:guid}/cancel", async (Guid id, Guid organizationId, ClaimsPrincipal user, CSweetDbContext db,
            IGenAiJobService service, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            return await service.CancelAsync(id, organizationId, cancellationToken: token) is { } job ? Results.Ok(job) : Results.NotFound();
        });

        var media = endpoints.MapGroup("/api/media-assets");
        media.MapPost("/uploads/organization/{organizationId:guid}", async (Guid organizationId,
            CreateMediaUploadSessionRequest request, ClaimsPrincipal user, CSweetDbContext db,
            IResumableMediaUploadService uploads, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            try { return Results.Ok(await uploads.CreateAsync(organizationId, request, token)); }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { errorCode = "invalid_upload", message = exception.Message });
            }
        });
        media.MapGet("/uploads/{sessionId:guid}", async (Guid sessionId, Guid organizationId,
            ClaimsPrincipal user, CSweetDbContext db, IResumableMediaUploadService uploads,
            CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            return await uploads.GetAsync(organizationId, sessionId, token) is { } session
                ? Results.Ok(session) : Results.NotFound();
        });
        media.MapPut("/uploads/{sessionId:guid}/chunks/{offset:long}", async (Guid sessionId, long offset,
            Guid organizationId, HttpRequest request, ClaimsPrincipal user, CSweetDbContext db,
            IResumableMediaUploadService uploads, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            if (request.ContentLength is not { } contentLength)
                return Results.BadRequest(new { errorCode = "content_length_required", message = "Each chunk requires Content-Length." });
            try
            {
                return Results.Ok(await uploads.AppendAsync(organizationId, sessionId, offset, contentLength,
                    request.Body, token));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { errorCode = "invalid_upload_offset", message = exception.Message });
            }
        });
        media.MapPost("/uploads/{sessionId:guid}/complete", async (Guid sessionId, Guid organizationId,
            ClaimsPrincipal user, CSweetDbContext db, IResumableMediaUploadService uploads,
            CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            try { return Results.Ok(await uploads.CompleteAsync(organizationId, sessionId, token)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { errorCode = "upload_completion_failed", message = exception.Message });
            }
        });
        media.MapDelete("/uploads/{sessionId:guid}", async (Guid sessionId, Guid organizationId,
            ClaimsPrincipal user, CSweetDbContext db, IResumableMediaUploadService uploads,
            CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            await uploads.CancelAsync(organizationId, sessionId, token);
            return Results.NoContent();
        });
        media.MapPost("/organization/{organizationId:guid}", async (Guid organizationId, IFormFile file, ClaimsPrincipal user,
            CSweetDbContext db, IMediaAssetService service, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            try
            {
                await using var stream = file.OpenReadStream();
                return Results.Ok(await service.SaveUploadAsync(organizationId, file.FileName, file.ContentType, stream, token));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { errorCode = "invalid_media", message = exception.Message });
            }
        });
        media.MapGet("/{id:guid}", async (Guid id, Guid organizationId, ClaimsPrincipal user, CSweetDbContext db,
            IMediaAssetService service, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            return await service.GetAsync(id, organizationId, token) is { } asset ? Results.Ok(asset) : Results.NotFound();
        });
        media.MapGet("/{id:guid}/content", async (Guid id, Guid organizationId, ClaimsPrincipal user, CSweetDbContext db,
            IMediaAssetService service, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            return await service.OpenReadAsync(id, organizationId, token) is { } mediaFile
                ? Results.Stream(mediaFile.Content, mediaFile.Asset.ContentType, mediaFile.Asset.FileName, enableRangeProcessing: true)
                : Results.NotFound();
        });
        media.MapDelete("/{id:guid}", async (Guid id, Guid organizationId, ClaimsPrincipal user, CSweetDbContext db,
            IMediaAssetService service, CancellationToken token) =>
        {
            if (!await CanAccessAsync(user, organizationId, db, token)) return Results.Forbid();
            await service.DeleteAsync(id, organizationId, token);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static async Task<bool> CanAccessAsync(ClaimsPrincipal principal, Guid organizationId, CSweetDbContext db, CancellationToken token)
    {
        if (principal.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole)) return true;
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId) &&
            await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.ApplicationUserId == userId && x.IsActive, token);
    }
}
