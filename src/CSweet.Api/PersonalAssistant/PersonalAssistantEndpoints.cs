using System.Security.Claims;
using CSweet.Api.Auth;
using CSweet.Application.PersonalAssistant;
using CSweet.Contracts.PersonalAssistant;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.PersonalAssistant;

public static class PersonalAssistantEndpoints
{
    public static IEndpointRouteBuilder MapPersonalAssistantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistant = endpoints.MapGroup("/api/personal-assistant").RequireAuthorization();
        assistant.MapGet("/settings", (ClaimsPrincipal p, IPersonalAssistantService s, CancellationToken t) =>
            Run(p, u => s.GetSettingsAsync(u, t)));
        assistant.MapPut("/settings", (ConfigurePersonalAssistantRequest r, ClaimsPrincipal p, IPersonalAssistantService s, CancellationToken t) =>
            Run(p, u => s.ConfigureAsync(u, r, t)));
        assistant.MapGet("/messages", (Guid? draftId, ClaimsPrincipal p, IPersonalAssistantService s, CancellationToken t) =>
            Run(p, u => s.ListMessagesAsync(u, draftId, t)));
        assistant.MapPost("/messages", (SendPersonalAssistantMessageRequest r, ClaimsPrincipal p, IPersonalAssistantService s, CancellationToken t) =>
            Run(p, u => s.SendAsync(u, r, t)));

        var drafts = endpoints.MapGroup("/api/repository-onboarding").RequireAuthorization();
        drafts.MapPost("/", (ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.CreateAsync(u, t)));
        drafts.MapGet("/", (ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.ListAsync(u, t)));
        drafts.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.GetAsync(u, id, t)));
        drafts.MapPost("/{id:guid}/github/authorize", (Guid id, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.AuthorizeGitHubAsync(u, id, t)));
        drafts.MapPost("/{id:guid}/github/complete", (Guid id, CompleteDraftGitHubAuthorizationRequest r, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.CompleteGitHubAsync(u, id, r, t)));
        drafts.MapGet("/{id:guid}/github/accounts", (Guid id, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.AccountsAsync(u, id, t)));
        drafts.MapGet("/{id:guid}/github/repositories", (Guid id, long installationId, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.RepositoriesAsync(u, id, installationId, t)));
        drafts.MapPost("/{id:guid}/sources", (Guid id, SelectOnboardingRepositoriesRequest r, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.SelectAsync(u, id, r, t)));
        drafts.MapDelete("/{id:guid}/sources/{sourceId:guid}", (Guid id, Guid sourceId, long revision, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.RemoveAsync(u, id, sourceId, revision, t)));
        drafts.MapPost("/{id:guid}/upload", (Guid id, long revision, string fileName, HttpRequest request, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) =>
            Run(p, u => s.UploadAsync(u, id, revision, fileName, request.Body, t)))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(512L * 1024 * 1024));
        drafts.MapPost("/{id:guid}/analyze", (Guid id, AnalyzeOnboardingRepositoriesRequest r, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.AnalyzeAsync(u, id, r, t)));
        drafts.MapPut("/{id:guid}/review", (Guid id, SaveRepositoryBusinessReviewRequest r, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.SaveReviewAsync(u, id, r, t)));
        drafts.MapPost("/{id:guid}/confirm", (Guid id, ConfirmRepositoryBusinessRequest r, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.ConfirmAsync(u, id, r, t)));
        drafts.MapPost("/{id:guid}/retry", (Guid id, RepositoryDraftRevisionRequest r, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, u => s.RetryAsync(u, id, r.ExpectedRevision, t)));
        drafts.MapDelete("/{id:guid}", (Guid id, long revision, ClaimsPrincipal p, IRepositoryOnboardingService s, CancellationToken t) => Run(p, async u => { await s.DiscardAsync(u, id, revision, t); return new { discarded = true }; }));
        return endpoints;
    }

    private static async Task<IResult> Run<T>(ClaimsPrincipal principal, Func<Guid, Task<T>> action)
    {
        var user = principal.GetApplicationUserId();
        if (!user.HasValue) return Results.Unauthorized();
        try { return Results.Ok(await action(user.Value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "This draft or configuration changed. Reload before continuing." }); }
        catch (CSweet.Application.Setup.AgentInstallationException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (InvalidDataException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (InvalidOperationException e) { return Results.Conflict(new { message = e.Message }); }
    }
}
