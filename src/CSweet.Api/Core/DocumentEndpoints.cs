using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Core;

namespace CSweet.Api.Core;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/documents").RequireAuthorization();

        group.MapGet("", async (Guid organizationId, string? search, Guid? folderId, Guid? packageId,
            string? status, string? documentType, bool includeArchived, string? creatorOrSteward,
            Guid? originWorkItemId, DateTimeOffset? updatedFrom, DateTimeOffset? updatedTo, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) =>
            await ExecuteAsync(http, async actor => Results.Ok(await service.BrowseAsync(organizationId, actor,
                new ArtifactDocumentQuery(search, folderId, packageId, status, documentType, includeArchived,
                    creatorOrSteward, originWorkItemId, updatedFrom, updatedTo), token))));

        group.MapGet("/{artifactId:guid}", async (Guid organizationId, Guid artifactId, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) =>
            await ExecuteAsync(http, async actor => await service.GetAsync(organizationId, actor, artifactId, token) is { } item
                ? Results.Ok(item) : Results.NotFound()));

        group.MapPost("", async (Guid organizationId, CreateArtifactDocumentRequest request, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) =>
            await ExecuteAsync(http, async actor =>
            {
                var item = await service.CreateAsync(organizationId, actor, request, token);
                return Results.Created($"/api/organizations/{organizationId:D}/documents/{item.Document.Id:D}", item);
            }));

        group.MapPost("/{artifactId:guid}/revisions", async (Guid organizationId, Guid artifactId,
            CreateArtifactRevisionRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.ReviseAsync(organizationId, actor, artifactId, request, token))));

        group.MapPost("/{artifactId:guid}/submit", async (Guid organizationId, Guid artifactId,
            SubmitArtifactRevisionRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SubmitAsync(organizationId, actor, artifactId, request, token))));

        group.MapPost("/{artifactId:guid}/decisions", async (Guid organizationId, Guid artifactId,
            DecideArtifactRevisionRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.DecideAsync(organizationId, actor, artifactId, request, token))));

        group.MapPost("/{artifactId:guid}/move", async (Guid organizationId, Guid artifactId,
            MoveArtifactRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.MoveAsync(organizationId, actor, artifactId, request, token))));
        group.MapPost("/{artifactId:guid}/steward", async (Guid organizationId, Guid artifactId,
            ReassignArtifactStewardRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.ReassignStewardAsync(organizationId, actor, artifactId, request, token))));

        group.MapPost("/{artifactId:guid}/archive", async (Guid organizationId, Guid artifactId,
            ArtifactArchiveRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetArchivedAsync(organizationId, actor, artifactId, true, request, token))));
        group.MapPost("/{artifactId:guid}/restore", async (Guid organizationId, Guid artifactId,
            ArtifactArchiveRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetArchivedAsync(organizationId, actor, artifactId, false, request, token))));

        group.MapGet("/folders", async (Guid organizationId, bool includeArchived, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) => await ExecuteAsync(http,
                async actor => Results.Ok(await service.ListFoldersAsync(organizationId, actor, includeArchived, token))));
        group.MapPost("/folders", async (Guid organizationId, CreateArtifactFolderRequest request, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) => await ExecuteAsync(http,
                async actor => Results.Ok(await service.CreateFolderAsync(organizationId, actor, request, token))));
        group.MapPut("/folders/{folderId:guid}", async (Guid organizationId, Guid folderId,
            UpdateArtifactFolderRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.UpdateFolderAsync(organizationId, actor, folderId, request, token))));
        group.MapPost("/folders/{folderId:guid}/archive", async (Guid organizationId, Guid folderId,
            ArtifactArchiveRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetFolderArchivedAsync(organizationId, actor, folderId, true, request, token))));
        group.MapPost("/folders/{folderId:guid}/restore", async (Guid organizationId, Guid folderId,
            ArtifactArchiveRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetFolderArchivedAsync(organizationId, actor, folderId, false, request, token))));

        group.MapPut("/{artifactId:guid}/grants", async (Guid organizationId, Guid artifactId,
            UpsertArtifactGrantRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetGrantsAsync(organizationId, actor, artifactId, request, token))));
        group.MapPost("/{artifactId:guid}/access-requests", async (Guid organizationId, Guid artifactId,
            RequestArtifactAccessRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.RequestAccessAsync(organizationId, actor, artifactId, request, token))));
        group.MapPost("/access-requests/{requestId:guid}/decision", async (Guid organizationId, Guid requestId,
            DecideArtifactAccessRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.DecideAccessAsync(organizationId, actor, requestId, request, token))));

        group.MapPost("/packages", async (Guid organizationId, CreateArtifactPackageRequest request, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) => await ExecuteAsync(http,
                async actor => Results.Ok(await service.CreatePackageAsync(organizationId, actor, request, token))));
        group.MapGet("/packages", async (Guid organizationId, bool includeArchived, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) => await ExecuteAsync(http,
                async actor => Results.Ok(await service.ListPackagesAsync(organizationId, actor, includeArchived, token))));
        group.MapGet("/packages/{packageId:guid}", async (Guid organizationId, Guid packageId, HttpContext http,
            IArtifactDocumentService service, CancellationToken token) => await ExecuteAsync(http,
                async actor => await service.GetPackageAsync(organizationId, actor, packageId, token) is { } package
                    ? Results.Ok(package) : Results.NotFound()));
        group.MapPost("/packages/{packageId:guid}/submit", async (Guid organizationId, Guid packageId,
            SubmitArtifactPackageRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SubmitPackageAsync(organizationId, actor, packageId, request, token))));
        group.MapPost("/packages/{packageId:guid}/decision", async (Guid organizationId, Guid packageId,
            DecideArtifactPackageRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.DecidePackageAsync(organizationId, actor, packageId, request, token))));
        group.MapPost("/packages/{packageId:guid}/archive", async (Guid organizationId, Guid packageId,
            ArtifactArchiveRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetPackageArchivedAsync(organizationId, actor, packageId, true, request, token))));
        group.MapPost("/packages/{packageId:guid}/restore", async (Guid organizationId, Guid packageId,
            ArtifactArchiveRequest request, HttpContext http, IArtifactDocumentService service,
            CancellationToken token) => await ExecuteAsync(http, async actor => Results.Ok(
                await service.SetPackageArchivedAsync(organizationId, actor, packageId, false, request, token))));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(HttpContext http, Func<ArtifactHumanActor, Task<IResult>> action)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return Results.Unauthorized();
        try { return await action(new ArtifactHumanActor(applicationUserId.Value)); }
        catch (UnauthorizedAccessException exception) { return Results.Json(new { errorCode = "not_authorized", message = exception.Message }, statusCode: 403); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException exception) { return Results.Conflict(new { errorCode = "revision_conflict", message = exception.Message }); }
        catch (ArgumentException exception) { return Results.BadRequest(new { errorCode = "validation_error", message = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { errorCode = "invalid_state", message = exception.Message }); }
    }
}
