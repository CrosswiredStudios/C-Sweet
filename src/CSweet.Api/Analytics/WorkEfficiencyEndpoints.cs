using System.Text;
using CSweet.Api.Auth;
using CSweet.Application.Analytics;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Analytics;

public static class WorkEfficiencyEndpoints
{
    public static IEndpointRouteBuilder MapWorkEfficiencyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/organizations/{organizationId:guid}/analytics/efficiency");
        group.AddEndpointFilter(async (context, next) =>
        {
            var user = context.HttpContext.User.GetApplicationUserId();
            if (!user.HasValue) return Results.Unauthorized();
            var organizationId = Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);
            var db = context.HttpContext.RequestServices.GetRequiredService<CSweetDbContext>();
            if (!await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == organizationId &&
                    x.ApplicationUserId == user && x.IsActive && x.EmployeeType == EmployeeType.Human &&
                    (x.PermissionLevel == OrganizationPermissionLevel.Owner || x.PermissionLevel == OrganizationPermissionLevel.Manager),
                    context.HttpContext.RequestAborted)) return Results.Forbid();
            try { return await next(context); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        });
        group.MapGet("", async (Guid organizationId, DateTimeOffset? from, DateTimeOffset? to,
            IWorkEfficiencyService service, CancellationToken ct) => Results.Ok(await service.GetAsync(organizationId, from, to, ct)));
        group.MapGet("/activity", async (Guid organizationId, Guid? workItemId, Guid? workstreamId, int? offset,
            int? limit, IWorkEfficiencyService service, CancellationToken ct) =>
            Results.Ok(await service.GetActivityAsync(organizationId, workItemId, workstreamId, offset ?? 0, limit ?? 50, ct)));
        group.MapGet("/export", async (Guid organizationId, DateTimeOffset? from, DateTimeOffset? to,
            IWorkEfficiencyService service, CancellationToken ct) =>
        {
            var report = await service.GetAsync(organizationId, from, to, ct);
            var csv = new StringBuilder("Kind,Title,Status,Direct tokens,Total tokens,Model calls,Reported calls,Legacy calls,Lead time ms,Cycle time ms,Partial\r\n");
            foreach (var r in report.Projects.Concat(report.WorkItems))
                csv.AppendLine($"{r.Kind},{Csv(r.Title)},{r.Status},{r.Direct.TotalTokens},{r.Total.TotalTokens},{r.Total.ModelCalls},{r.Total.FullyReportedCalls},{r.Total.LegacyCalls},{r.Lifecycle.LeadTimeMs},{r.Lifecycle.CycleTimeMs},{report.Truncated}");
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "work-efficiency.csv");
        });
        return routes;
    }

    internal static string Csv(string value) => "\"" + (value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
}
