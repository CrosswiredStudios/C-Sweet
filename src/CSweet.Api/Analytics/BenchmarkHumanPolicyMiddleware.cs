using CSweet.Api.Auth;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Analytics;

public sealed class BenchmarkHumanPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, CSweetDbContext db)
    {
        if (http.User.GetApplicationUserId().HasValue && http.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            var path = http.Request.Path.Value ?? "";
            var organization = await ResolveOrganizationAsync(http, db);
            var approval = path.EndsWith("/approve", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/decide", StringComparison.OrdinalIgnoreCase);
            // Reading chat state is operational housekeeping, not an intervention.
            var readReceipt = path.EndsWith("/read", StringComparison.OrdinalIgnoreCase);
            if (organization.HasValue && !readReceipt && !await BenchmarkHumanPolicy.AllowsAsync(db, organization.Value, approval, http.RequestAborted))
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsJsonAsync(new { error = "This benchmark's assistance policy does not allow this intervention. Cancel the trial to change its conditions." });
                return;
            }
        }
        await next(http);
    }

    private static async Task<Guid?> ResolveOrganizationAsync(HttpContext http, CSweetDbContext db)
    {
        if (Guid.TryParse(http.Request.RouteValues["organizationId"]?.ToString(), out var organization)) return organization;
        // Legacy routes identify the resource instead of the business. Resolve persisted ownership,
        // never an organization supplied in a request body.
        var path = http.Request.Path.Value ?? "";
        var keys = new[] { "id", "artifactId", "taskId", "workItemId", "conversationId", "employeeId", "workstreamId" };
        foreach (var key in keys)
        {
            if (!Guid.TryParse(http.Request.RouteValues[key]?.ToString(), out var id)) continue;
            IQueryable<Guid?>? owner = path.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase)
                ? db.CoreArtifacts.Where(x => x.Id == id).Select(x => (Guid?)x.OrganizationId)
                : path.Contains("/tasks/", StringComparison.OrdinalIgnoreCase)
                ? db.CoreWorkTasks.Where(x => x.Id == id).Select(x => (Guid?)x.OrganizationId)
                : path.Contains("/conversations/", StringComparison.OrdinalIgnoreCase)
                ? db.CoreConversations.Where(x => x.Id == id).Select(x => (Guid?)x.OrganizationId)
                : path.Contains("/employees/", StringComparison.OrdinalIgnoreCase)
                ? db.CoreOrganizationUsers.Where(x => x.Id == id).Select(x => (Guid?)x.OrganizationId)
                : path.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)
                ? db.CoreOrganizations.Where(x => x.Id == id).Select(x => (Guid?)x.Id) : null;
            if (owner is not null && await owner.SingleOrDefaultAsync(http.RequestAborted) is { } found) return found;
        }
        return null;
    }
}
