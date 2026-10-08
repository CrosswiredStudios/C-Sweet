using CSweet.Application.Core;
using CSweet.Api.Auth;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CSweet.Api.Core;

public static class AgentMemoryEndpoints
{
    public static IEndpointRouteBuilder MapAgentMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory");
        group.MapMemoryTransferRoutes();
        group.MapMemoryProcedureReviewRoutes();
        group.MapMemoryCoreReviewRoutes();
        group.MapMemoryLegacyReviewRoutes();
        group.MapMemorySuppressionRoutes();
        group.MapMemoryHoldRoutes();
        group.MapMemoryErasureImpactRoutes();

        group.MapGet("/history/{kind}/{recordId:guid}", async (Guid organizationId, Guid employeeId, string kind, Guid recordId,
            long? afterRevision, int? limit, ClaimsPrincipal principal, IAgentMemoryReviewService review, CancellationToken token) =>
        {
            var userId = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await review.ReadHistoryAsync(organizationId, employeeId, kind, recordId, userId.Value, afterRevision ?? 0, limit ?? 20, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_history_request" }); }
            catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_history_unavailable" }); }
            catch (System.Text.Json.JsonException) { return Results.Conflict(new { error = "memory_history_unavailable" }); }
        });

        group.MapGet("/claims/{claimId:guid}/review/entities", async (Guid organizationId, Guid employeeId, Guid claimId, string? search,
            ClaimsPrincipal principal, IAgentMemoryReviewService review, CancellationToken token) =>
        {
            var userId = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await review.FindClaimCorrectionTargetsAsync(organizationId, employeeId, claimId, userId.Value, search ?? "", token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_review_changed" }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_entity_search" }); }
            catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_review_source_unavailable" }); }
        });

        group.MapGet("/claims/{claimId:guid}/review", async (Guid organizationId, Guid employeeId, Guid claimId,
            ClaimsPrincipal principal, IAgentMemoryReviewService review, CancellationToken token) =>
        {
            var userId = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await review.GetClaimAsync(organizationId, employeeId, claimId, userId.Value, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_review_source_unavailable" }); }
        });

        group.MapPost("/claims/{claimId:guid}/review", async (Guid organizationId, Guid employeeId, Guid claimId,
            ReviewMemoryClaimRequest request, ClaimsPrincipal principal, IAgentMemoryReviewService review, CancellationToken token) =>
        {
            var userId = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await review.ReviewClaimAsync(organizationId, employeeId, claimId, userId.Value, request, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_review_changed" }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_review" }); }
            catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_review_source_unavailable" }); }
        });

        group.MapGet("/enrichment/failures", async (Guid organizationId, Guid employeeId, string? cursor, int? limit,
            ClaimsPrincipal principal, IAgentMemoryRecoveryService recovery, CancellationToken token) =>
        {
            var userId = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await recovery.ListFailuresAsync(organizationId, employeeId, userId.Value, cursor, limit ?? 20, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_cursor" }); }
        });

        group.MapPost("/enrichment/{jobId:guid}/retry", async (Guid organizationId, Guid employeeId, Guid jobId,
            RetryMemoryEnrichmentRequest request, ClaimsPrincipal principal, IAgentMemoryRecoveryService recovery, CancellationToken token) =>
        {
            var userId = principal.Identity?.IsAuthenticated == true ? principal.GetApplicationUserId() : null;
            if (!userId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await recovery.RetryAsync(organizationId, employeeId, jobId, userId.Value, request, token)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "memory_job_changed" }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory_retry" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "memory_source_unavailable" }); }
        });

        group.MapGet("/summary", async (Guid organizationId, Guid employeeId, ClaimsPrincipal principal,
            IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy, CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var summary = await memory.GetSummaryAsync(organizationId, employeeId, cancellationToken);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });

        group.MapGet("/items", async (
            Guid organizationId, Guid employeeId, string? kind, string? layer, string? search, Guid? userId,
            string? scope, string? @namespace, string? source, string? sensitivity, string? state, string? confirmationState,
            DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit,
            ClaimsPrincipal principal, IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var page = await memory.BrowseAsync(organizationId, employeeId,
                new AgentMemoryQuery(kind ?? layer, search, userId, scope ?? @namespace, source, sensitivity,
                    state ?? confirmationState, from, to, cursor, limit ?? 50),
                cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });

        group.MapGet("/items/{memoryId:guid}", async (
            Guid organizationId, Guid employeeId, Guid memoryId,
            ClaimsPrincipal principal, IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var item = await memory.GetItemAsync(organizationId, employeeId, memoryId, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/graph", async (
            Guid organizationId, Guid employeeId, string? search, Guid? userId, int? limit,
            ClaimsPrincipal principal, IAgentMemoryService memory, IEmployeeHierarchyAccessService hierarchy,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInspectAsync(organizationId, employeeId, principal, hierarchy, cancellationToken)) return Results.Forbid();
            var graph = await memory.GetGraphAsync(organizationId, employeeId, search, userId, limit ?? 100, cancellationToken);
            return graph is null ? Results.NotFound() : Results.Ok(graph);
        });

        return endpoints;
    }

    private static async Task<bool> CanInspectAsync(Guid organizationId, Guid employeeId,
        ClaimsPrincipal principal, IEmployeeHierarchyAccessService hierarchy, CancellationToken token)
    {
        var applicationUserId = principal.GetApplicationUserId();
        if (!applicationUserId.HasValue) return false;
        var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
            applicationUserId.Value, token);
        return actorId.HasValue && await hierarchy.CanAccessSensitiveAsync(
            organizationId, actorId.Value, employeeId, token);
    }
}
