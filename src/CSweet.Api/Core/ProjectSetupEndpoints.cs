using CSweet.Api.Auth;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public static class ProjectSetupEndpoints
{
    public static IEndpointRouteBuilder MapProjectSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/core/organizations/{organizationId:guid}/project-setup").RequireAuthorization();
        group.MapGet("/options", (Guid organizationId, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy, CancellationToken ct) =>
            Run(organizationId, http, setup, db, policy, false, a => setup.OptionsAsync(a, ct), ct));
        group.MapGet("/intakes/{intakeId:guid}", (Guid organizationId, Guid intakeId, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy, CancellationToken ct) =>
            Run(organizationId, http, setup, db, policy, false, a => setup.DraftAsync(a, intakeId, ct), ct));
        group.MapPost("/projects", (Guid organizationId, CreateProjectRequest request, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy, CancellationToken ct) =>
            Run(organizationId, http, setup, db, policy, true, a => setup.CreateAsync(a, request, ct), ct));
        group.MapGet("/projects/{projectId:guid}/members", (Guid organizationId, Guid projectId, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy, CancellationToken ct) =>
            Run(organizationId, http, setup, db, policy, false, a => setup.MembersAsync(a, projectId, ct), ct));
        group.MapPut("/projects/{projectId:guid}/members", (Guid organizationId, Guid projectId, UpdateProjectMembersRequest request, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy, CancellationToken ct) =>
            Run(organizationId, http, setup, db, policy, true, a => setup.UpdateMembersAsync(a, projectId, request, ct), ct));
        group.MapPut("/projects/{projectId:guid}/status", (Guid organizationId, Guid projectId, ChangeProjectStatusRequest request, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy, CancellationToken ct) =>
            Run(organizationId, http, setup, db, policy, true, a => setup.ChangeStatusAsync(a, projectId, request, ct), ct));
        return app;
    }
    private static async Task<IResult> Run<T>(Guid org, HttpContext http, ProjectSetupService setup, CSweetDbContext db, ProjectWorkPolicy policy,
        bool write, Func<CSweet.Domain.Core.OrganizationUser, Task<T>> action, CancellationToken ct)
    {
        if (http.User.GetApplicationUserId() is not { } user) return Results.Forbid();
        try
        {
            await using var transaction = write && db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (write) await policy.LockAsync(org, ct);
            var actor = await setup.HumanAsync(org, user, ct);
            var result = await action(actor);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException e) { return Results.NotFound(new { error = e.Message }); }
        catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateConcurrencyException) { return Results.Conflict(new { error = e.Message }); }
    }
}
