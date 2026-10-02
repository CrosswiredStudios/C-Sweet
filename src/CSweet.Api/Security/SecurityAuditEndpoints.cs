using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Contracts.Security;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Security;

public static class SecurityAuditEndpoints
{
    public static IEndpointRouteBuilder MapSecurityAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/security/events");

        group.MapGet("", async (
            Guid organizationId, string? cursor, int? limit, DateTimeOffset? from, DateTimeOffset? to,
            string? category, string? direction, string? outcome, string? actorKind, string? search,
            bool? groupModelResponses, HttpContext http, CSweetDbContext db, IAuditEventWriter audit,
            ISecurityAuditService service, CancellationToken cancellationToken) =>
        {
            var access = await AuthorizeAsync(organizationId, http, db, cancellationToken);
            if (!await RecordAccessAsync(audit, organizationId, access, "security.timeline.viewed",
                    access.Allowed ? "Accepted" : "Denied", new { cursor, limit, from, to, category, direction, outcome, actorKind, search, groupModelResponses }, cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!access.Authenticated) return Results.Unauthorized();
            if (!access.Allowed) return Results.Forbid();
            return Results.Ok(await service.BrowseAsync(organizationId,
                new SecurityEventQuery(cursor, limit ?? 50, from, to, category, direction, outcome, actorKind, search,
                    GroupModelResponses: groupModelResponses ?? true),
                cancellationToken));
        });

        group.MapGet("/{eventId:guid}", async (
            Guid organizationId, Guid eventId, bool? groupModelResponse, HttpContext http, CSweetDbContext db,
            IAuditEventWriter audit, ISecurityAuditService service, CancellationToken cancellationToken) =>
        {
            var access = await AuthorizeAsync(organizationId, http, db, cancellationToken);
            if (!await RecordAccessAsync(audit, organizationId, access, "security.event.viewed",
                    access.Allowed ? "Accepted" : "Denied", new { eventId, groupModelResponse }, cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!access.Authenticated) return Results.Unauthorized();
            if (!access.Allowed) return Results.Forbid();
            var item = await service.GetAsync(organizationId, eventId, cancellationToken,
                groupModelResponse: groupModelResponse ?? false);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        return endpoints;
    }

    private static async Task<SecurityAccess> AuthorizeAsync(
        Guid organizationId,
        HttpContext http,
        CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return new(false, false, null, null);
        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive,
            cancellationToken);
        var allowed = member is { EmployeeType: EmployeeType.Human } &&
            member.PermissionLevel is OrganizationPermissionLevel.Owner or OrganizationPermissionLevel.Manager;
        return new(true, allowed, applicationUserId, member);
    }

    private static async Task<bool> RecordAccessAsync(
        IAuditEventWriter audit,
        Guid organizationId,
        SecurityAccess access,
        string eventType,
        string outcome,
        object metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            await audit.AppendAsync(new AuditEventWriteRequest(
                eventType, "SecurityAccess", "Inbound", outcome, organizationId,
                "AuditEvent", Summary: access.Allowed
                    ? "A human manager opened security audit data."
                    : "Access to security audit data was denied.",
                MetadataJson: JsonSerializer.Serialize(metadata),
                Actor: new AuditActor(access.Member?.EmployeeType == EmployeeType.Agent ? "Agent" : "Human", access.Authenticated,
                    access.ApplicationUserId, access.Member?.Id,
                    access.Member?.DisplayName ?? access.ApplicationUserId?.ToString("D")),
                ErrorCode: access.Allowed ? null : "security_access_denied"), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private sealed record SecurityAccess(
        bool Authenticated,
        bool Allowed,
        Guid? ApplicationUserId,
        OrganizationUser? Member);
}
