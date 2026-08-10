using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Application.Analytics;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Analytics;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class EmployeeEndpoints
{
    public static IEndpointRouteBuilder MapEmployeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(
            "/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}");

        group.MapGet("/details", async (Guid organizationId, Guid employeeId, HttpContext http,
            IEmployeeDetailsService service, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.GetAsync(organizationId, employeeId,
                applicationUserId.Value, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapPut("/profile", async (Guid organizationId, Guid employeeId,
            UpdateEmployeeProfileRequest request, HttpContext http,
            IEmployeeDetailsService service, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            try { return Results.Ok(await service.UpdateProfileAsync(organizationId, employeeId,
                applicationUserId.Value, request, cancellationToken)); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException exception)
            { return Results.Conflict(new { error = "revision_conflict", message = exception.Message }); }
            catch (ArgumentException exception)
            { return Results.BadRequest(new { error = "invalid_employee_profile", message = exception.Message }); }
        });

        group.MapGet("/personal-board", async (Guid organizationId, Guid employeeId, bool? includeArchived,
            HttpContext http, IEmployeeHierarchyAccessService hierarchy, IPersonalTodoService personal,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
                applicationUserId.Value, cancellationToken);
            if (!actorId.HasValue) return Results.Forbid();
            if (!await hierarchy.CanAccessSensitiveAsync(organizationId, actorId.Value, employeeId,
                cancellationToken)) return Results.Forbid();
            try
            {
                await personal.EnsureBoardAsync(organizationId, employeeId, cancellationToken);
                var directory = await personal.ListAsync(organizationId,
                    new PersonalTodoActor(actorId.Value, null), includeArchived ?? false, cancellationToken);
                var board = directory.Boards.SingleOrDefault(x => x.OwnerOrganizationUserId == employeeId);
                return board is null ? Results.NotFound() : Results.Ok(board);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException) { return Results.NotFound(); }
        });

        group.MapGet("/assignments", async (Guid organizationId, Guid employeeId,
            HttpContext http, IEmployeeHierarchyAccessService hierarchy,
            IEmployeeAssignedWorkQueryService assignedWork, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
                applicationUserId.Value, cancellationToken);
            if (!actorId.HasValue) return Results.Forbid();
            if (!await hierarchy.CanAccessSensitiveAsync(organizationId, actorId.Value, employeeId,
                cancellationToken)) return Results.Forbid();
            try { return Results.Ok(await assignedWork.GetAsync(organizationId, employeeId,
                actorId.Value, cancellationToken)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapGet("/runtime", async (Guid organizationId, Guid employeeId, HttpContext http,
            CSweetDbContext db, IEmployeeHierarchyAccessService hierarchy,
            IAgentInteractiveRuntimeService runtime, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
                applicationUserId.Value, cancellationToken);
            if (!actorId.HasValue || !await hierarchy.CanAccessSensitiveAsync(organizationId,
                actorId.Value, employeeId, cancellationToken)) return Results.Forbid();
            var installationId = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                x.Id == employeeId && x.OrganizationId == organizationId && x.IsActive)
                .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(cancellationToken);
            return installationId.HasValue
                ? Results.Ok(await runtime.GetStatusAsync(installationId.Value, cancellationToken))
                : Results.NotFound();
        });

        group.MapGet("/operations", async (Guid organizationId, Guid employeeId, HttpContext http,
            CSweetDbContext db, IEmployeeHierarchyAccessService hierarchy,
            IAgentInstallationService installations, CancellationToken cancellationToken) =>
        {
            var installationId = await ResolveAccessibleInstallationAsync(organizationId, employeeId,
                http, hierarchy, db, cancellationToken);
            if (!installationId.HasValue) return Results.Forbid();
            var installation = await installations.GetAsync(installationId.Value, cancellationToken);
            return installation is null ? Results.NotFound() : Results.Ok(installation);
        });

        group.MapGet("/runtime-history", async (Guid organizationId, Guid employeeId, HttpContext http,
            CSweetDbContext db, IEmployeeHierarchyAccessService hierarchy,
            IAgentInstallationService installations, CancellationToken cancellationToken) =>
        {
            var installationId = await ResolveAccessibleInstallationAsync(organizationId, employeeId,
                http, hierarchy, db, cancellationToken);
            return installationId.HasValue
                ? Results.Ok(await installations.ListRunsAsync(installationId.Value, cancellationToken))
                : Results.Forbid();
        });

        group.MapGet("/build-log", async (Guid organizationId, Guid employeeId, HttpContext http,
            CSweetDbContext db, IEmployeeHierarchyAccessService hierarchy,
            IAgentInstallationService installations, CancellationToken cancellationToken) =>
        {
            var installationId = await ResolveAccessibleInstallationAsync(organizationId, employeeId,
                http, hierarchy, db, cancellationToken);
            if (!installationId.HasValue) return Results.Forbid();
            var log = await installations.GetBuildLogAsync(installationId.Value, cancellationToken);
            return log is null ? Results.NotFound() : Results.Ok(log);
        });

        group.MapGet("/configuration", async (Guid organizationId, Guid employeeId,
            HttpContext http, CSweetDbContext db, IEmployeeHierarchyAccessService hierarchy,
            IAgentConfigurationService configuration, CancellationToken cancellationToken) =>
        {
            var installationId = await ResolveAccessibleInstallationAsync(organizationId, employeeId,
                http, hierarchy, db, cancellationToken);
            if (!installationId.HasValue) return Results.Forbid();
            try { return Results.Ok(await configuration.GetEmployeeAsync(
                organizationId, employeeId, cancellationToken)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapGet("/usage", async (Guid organizationId, Guid employeeId, string? window,
            HttpContext http, IEmployeeHierarchyAccessService hierarchy,
            IInferenceAnalyticsService analytics, CancellationToken cancellationToken) =>
        {
            var applicationUserId = http.User.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
                applicationUserId.Value, cancellationToken);
            if (!actorId.HasValue || !await hierarchy.CanAccessSensitiveAsync(organizationId,
                actorId.Value, employeeId, cancellationToken)) return Results.Forbid();
            var parsed = window?.Trim().ToLowerInvariant() switch
            {
                null or "" or "30d" => InferenceAnalyticsWindow.Last30Days,
                "24h" => InferenceAnalyticsWindow.Last24Hours,
                "7d" => InferenceAnalyticsWindow.Last7Days,
                _ => (InferenceAnalyticsWindow?)null
            };
            if (!parsed.HasValue) return Results.BadRequest(new { error = "Window must be 24h, 7d, or 30d." });
            var result = await analytics.GetAsync(organizationId, parsed.Value, cancellationToken);
            var rows = result.Employees.Where(x => x.EmployeeId == employeeId).ToList();
            return Results.Ok(result with { Employees = rows, Totals = new InferenceAnalyticsTotalsResponse(
                rows.Sum(x => x.RequestCount), rows.Sum(x => x.InputTokens),
                rows.Sum(x => x.OutputTokens), rows.Sum(x => x.TotalTokens)) });
        });

        return endpoints;
    }

    private static async Task<Guid?> ResolveAccessibleInstallationAsync(Guid organizationId,
        Guid employeeId, HttpContext http, IEmployeeHierarchyAccessService hierarchy,
        CSweetDbContext db, CancellationToken token)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return null;
        var actorId = await hierarchy.ResolveOrganizationUserIdAsync(organizationId,
            applicationUserId.Value, token);
        if (!actorId.HasValue || !await hierarchy.CanAccessSensitiveAsync(organizationId,
            actorId.Value, employeeId, token)) return null;
        return await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employeeId &&
            x.OrganizationId == organizationId && x.IsActive).Select(x => x.AgentInstallationId)
            .SingleOrDefaultAsync(token);
    }
}
