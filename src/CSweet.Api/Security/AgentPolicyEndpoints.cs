using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Security;

/// <summary>
/// Read-only inspection of the temporal policy counter plus a dry threshold check. Nothing here
/// mutates policy state: recording happens in the service when an operation is admitted, so these
/// endpoints cannot be used to burn another installation's budget.
/// </summary>
public static class AgentPolicyEndpoints
{
    /// <summary>Mirrors <see cref="AgentPolicyRules.MaximumWindow"/> (30 days) for a clear 400.</summary>
    private const int MaximumWindowMinutes = 43200;

    private const int DefaultWindowMinutes = 60;

    public static IEndpointRouteBuilder MapAgentPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/security/agent-policy").RequireAuthorization();

        group.MapGet("/events", async (
            string? businessId, Guid? installationId, string? kind, int? windowMinutes, int? limit,
            HttpContext http, CSweetDbContext db, IAuditEventWriter audit, IAgentPolicyService policy,
            CancellationToken cancellationToken) =>
        {
            var access = await AuthorizeAsync(businessId, installationId, http, db, cancellationToken);
            if (!await RecordAccessAsync(audit, access, "security.agent_policy.events.viewed",
                    access.Allowed ? "Accepted" : "Denied",
                    new { businessId, installationId, kind, windowMinutes, limit }, cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!access.Allowed) return Reject(access);
            if (windowMinutes is <= 0 or > MaximumWindowMinutes)
                return Results.BadRequest(new
                {
                    error = $"windowMinutes must be between 1 and {MaximumWindowMinutes}."
                });

            var page = await policy.BrowseAsync(new AgentPolicyEventQuery(
                access.OrganizationId.Value.ToString("D"),
                installationId,
                kind,
                TimeSpan.FromMinutes(windowMinutes ?? DefaultWindowMinutes),
                limit ?? 100), cancellationToken);
            return Results.Ok(page);
        });

        group.MapPost("/evaluate", async (
            AgentPolicyEvaluateRequest request, HttpContext http, CSweetDbContext db, IAuditEventWriter audit,
            IAgentPolicyService policy, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var access = await AuthorizeAsync(request.BusinessId, request.InstallationId, http, db, cancellationToken);
            if (!await RecordAccessAsync(audit, access, "security.agent_policy.evaluated",
                    access.Allowed ? "Accepted" : "Denied",
                    new { request.Kind, request.WindowMinutes, request.Maximum, request.InstallationId },
                    cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!access.Allowed) return Reject(access);
            if (string.IsNullOrWhiteSpace(request.Kind))
                return Results.BadRequest(new { error = "kind is required." });
            if (request.WindowMinutes is <= 0 or > MaximumWindowMinutes)
                return Results.BadRequest(new
                {
                    error = $"windowMinutes must be between 1 and {MaximumWindowMinutes}."
                });
            if (request.Maximum < 0)
                return Results.BadRequest(new { error = "maximum must not be negative." });

            var threshold = new AgentPolicyThreshold(
                request.Kind, TimeSpan.FromMinutes(request.WindowMinutes), request.Maximum);
            var candidate = new AgentPolicyEvent(
                request.Kind,
                request.InstallationId,
                access.OrganizationId.Value.ToString("D"),
                request.Risk,
                request.OccurredAt ?? clock.GetUtcNow());
            return Results.Ok(await policy.CheckAsync(threshold, candidate, cancellationToken));
        });

        return endpoints;
    }

    private static IResult Reject(PolicyAccess access) => access.State switch
    {
        PolicyAccessState.Unauthenticated => Results.Unauthorized(),
        PolicyAccessState.MissingBusiness => Results.BadRequest(new
        {
            error = "A businessId — or an installationId that resolves to one — is required; policy counts are never cross-tenant."
        }),
        _ => Results.Forbid()
    };

    /// <summary>
    /// Same shape as the security timeline's rule: an active human owner or manager of the
    /// business, and (when asked) an installation that genuinely belongs to that same business.
    /// </summary>
    private static async Task<PolicyAccess> AuthorizeAsync(
        string? businessId,
        Guid? installationId,
        HttpContext http,
        CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue)
            return new(PolicyAccessState.Unauthenticated, null, null, null);

        // The business can be named directly, or derived from the installation being inspected.
        // Either way the tenant is fixed before any count is read.
        var resolvedFromInstallation = false;
        if (!Guid.TryParse(businessId, out var organizationId))
        {
            if (installationId is not { } scope)
                return new(PolicyAccessState.MissingBusiness, null, applicationUserId, null);
            var ownerBusiness = await db.AgentInstallations.AsNoTracking()
                .Where(x => x.Id == scope)
                .Select(x => x.BusinessId)
                .SingleOrDefaultAsync(cancellationToken);
            if (!Guid.TryParse(ownerBusiness, out organizationId))
                return new(PolicyAccessState.MissingBusiness, null, applicationUserId, null);
            resolvedFromInstallation = true;
        }

        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive && x.ArchivedAt == null,
            cancellationToken);
        if (member is not
            {
                EmployeeType: EmployeeType.Human,
                PermissionLevel: OrganizationPermissionLevel.Owner or OrganizationPermissionLevel.Manager
            })
            return new(PolicyAccessState.Forbidden, organizationId, applicationUserId, member);

        if (!resolvedFromInstallation && installationId is { } installation)
        {
            // Compare parsed form so a differently formatted business id cannot slip past the check.
            var installationBusiness = await db.AgentInstallations.AsNoTracking()
                .Where(x => x.Id == installation)
                .Select(x => x.BusinessId)
                .SingleOrDefaultAsync(cancellationToken);
            if (!Guid.TryParse(installationBusiness, out var parsed) || parsed != organizationId)
                return new(PolicyAccessState.Forbidden, organizationId, applicationUserId, member);
        }

        return new(PolicyAccessState.Allowed, organizationId, applicationUserId, member);
    }

    private static async Task<bool> RecordAccessAsync(
        IAuditEventWriter audit,
        PolicyAccess access,
        string eventType,
        string outcome,
        object metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            await audit.AppendAsync(new AuditEventWriteRequest(
                eventType, "SecurityAccess", "Inbound", outcome, access.OrganizationId,
                "AgentPolicyEvent", Summary: access.State == PolicyAccessState.Allowed
                    ? "Policy counts were inspected."
                    : "Access to policy counts was denied.",
                MetadataJson: JsonSerializer.Serialize(metadata),
                Actor: new AuditActor(
                    access.Member?.EmployeeType == EmployeeType.Agent ? "Agent" : "Human",
                    access.State != PolicyAccessState.Unauthenticated,
                    access.ApplicationUserId, access.Member?.Id,
                    access.Member?.DisplayName ?? access.ApplicationUserId?.ToString("D")),
                ErrorCode: access.State == PolicyAccessState.Allowed ? null : "security_access_denied"),
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private enum PolicyAccessState
    {
        Allowed,
        Unauthenticated,
        MissingBusiness,
        Forbidden
    }

    private sealed record PolicyAccess(
        PolicyAccessState State,
        Guid? OrganizationId,
        Guid? ApplicationUserId,
        OrganizationUser? Member)
    {
        public bool Allowed => State == PolicyAccessState.Allowed;
    }
}
